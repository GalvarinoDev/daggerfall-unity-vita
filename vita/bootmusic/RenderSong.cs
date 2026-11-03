// Renders a DFU song with DFU's own synth (Assets/Scripts/AudioSynthesis) and SoundFont, the way DaggerfallSongPlayer plays it on PC
// (48 kHz stereo, polyphony 100, Gain 5) at the default MusicVolume 0.5, to 16-bit PCM for the boot music plugin, which rescales
// it by the user's MusicVolume / 0.5. Run through vita/bootmusic/render.sh.
//   RenderSong <soundfont.sf2> <song.mid> <seconds> <out.wav> <out.raw>
using System;
using System.IO;
using DaggerfallWorkshop.AudioSynthesis;
using DaggerfallWorkshop.AudioSynthesis.Midi;
using DaggerfallWorkshop.AudioSynthesis.Sequencer;
using DaggerfallWorkshop.AudioSynthesis.Synthesis;

class MemoryResource : IResource
{
    readonly byte[] data; readonly string name;
    public MemoryResource(string path) { data = File.ReadAllBytes(path); name = Path.GetFileName(path).Replace(".bytes", ""); }   // Unity resource copies end in .bytes; the synth picks the bank type from the extension
    public string GetName() { return name; }
    public bool DeleteAllowed() { return false; }
    public bool ReadAllowed() { return true; }
    public bool WriteAllowed() { return false; }
    public void DeleteResource() { }
    public Stream OpenResourceForRead() { return new MemoryStream(data); }
    public Stream OpenResourceForWrite() { return null; }
}

static class RenderSong
{
    const int SampleRate = 48000;
    const float Gain = 5 * 0.5f;     // DaggerfallSongPlayer.Gain x default MusicVolume

    static int Main(string[] args)
    {
        var synth = new Synthesizer(SampleRate, 2, 256, 4, 100);
        synth.LoadBank(new MemoryResource(args[0]));
        synth.ResetSynthControls();
        var sequencer = new MidiFileSequencer(synth);
        if (!sequencer.LoadMidi(new MidiFile(new MemoryResource(args[1]))))
            return 1;
        sequencer.Play();

        long frames = (long)(double.Parse(args[2]) * SampleRate);
        var buffer = new float[synth.WorkingBufferSize];
        var pcm = new MemoryStream();
        var w = new BinaryWriter(pcm);
        float peak = 0;
        int clipped = 0;
        for (long done = 0; done < frames; done += buffer.Length / 2)
        {
            sequencer.FillMidiEventQueue();
            synth.GetNext(buffer);
            foreach (float s in buffer)
            {
                float v = s * Gain;
                peak = Math.Max(peak, Math.Abs(v));
                if (v > 1 || v < -1) clipped++;
                w.Write((short)(Math.Max(-1f, Math.Min(1f, v)) * 32767));
            }
        }
        byte[] raw = pcm.ToArray();
        // Fade the last 3 s, so the plugin ends quietly if the engine load outlasts the file
        int fadeFrames = 3 * SampleRate, total = raw.Length / 4;
        for (int f = Math.Max(0, total - fadeFrames); f < total; f++)
            for (int c = 0; c < 2; c++)
            {
                int o = f * 4 + c * 2;
                short v = (short)(BitConverter.ToInt16(raw, o) * (total - f) / fadeFrames);
                raw[o] = (byte)v; raw[o + 1] = (byte)(v >> 8);
            }
        File.WriteAllBytes(args[4], raw);
        using (var wav = new BinaryWriter(File.Create(args[3])))
        {
            wav.Write("RIFF".ToCharArray()); wav.Write(36 + raw.Length); wav.Write("WAVEfmt ".ToCharArray());
            wav.Write(16); wav.Write((short)1); wav.Write((short)2); wav.Write(SampleRate); wav.Write(SampleRate * 4);
            wav.Write((short)4); wav.Write((short)16); wav.Write("data".ToCharArray()); wav.Write(raw.Length); wav.Write(raw);
        }
        Console.WriteLine("rendered {0:F1} s, {1} bytes, peak {2:F2}, clipped samples {3}", raw.Length / 4.0 / SampleRate, raw.Length, peak, clipped);
        return 0;
    }
}
