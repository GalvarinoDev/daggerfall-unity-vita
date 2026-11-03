// Renders DFU's MIDI songs offline with DFU's own synth (Assets/Scripts/AudioSynthesis), the way upstream DaggerfallSongPlayer
// plays them on PC (48 kHz stereo, 100 voices, Unity's default 1024 x 4 DSP buffer, output = synth * Gain, clamped like Unity's mixer).
// The song plays once, no tail: the player restarts it (LoadMidi + NoteOffAll) as soon as the sequencer stops.
// Usage: mono Render.exe <soundfont.sf2> <gain> <out-dir> <suffix> <song.mid>...   → <out-dir>/song_<song><suffix>.wav (16-bit PCM)
using System;
using System.IO;
using DaggerfallWorkshop.AudioSynthesis;
using DaggerfallWorkshop.AudioSynthesis.Midi;
using DaggerfallWorkshop.AudioSynthesis.Sequencer;
using DaggerfallWorkshop.AudioSynthesis.Synthesis;

class FileResource : IResource
{
    readonly byte[] data; readonly string name;
    public FileResource(string path) { data = File.ReadAllBytes(path); name = Path.GetFileName(path); }
    public string GetName() { return name; }
    public bool DeleteAllowed() { return false; }
    public bool ReadAllowed() { return true; }
    public bool WriteAllowed() { return false; }
    public void DeleteResource() { }
    public Stream OpenResourceForRead() { return new MemoryStream(data); }
    public Stream OpenResourceForWrite() { return null; }
}

static class Render
{
    const int SampleRate = 48000, Channels = 2, BufferLength = 1024, NumBuffers = 4, Polyphony = 100;

    static int Main(string[] args)
    {
        var bank = new FileResource(args[0]);
        float gain = float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        string outDir = args[2], suffix = args[3];
        for (int a = 4; a < args.Length; a++)
        {
            var synth = new Synthesizer(SampleRate, Channels, BufferLength / NumBuffers, NumBuffers, Polyphony);
            synth.LoadBank(bank);
            var seq = new MidiFileSequencer(synth);
            if (!seq.LoadMidi(new MidiFile(new FileResource(args[a]))))
            {
                Console.Error.WriteLine("cannot load " + args[a]);
                return 1;
            }
            seq.Play();
            var buffer = new float[synth.WorkingBufferSize];
            string outPath = Path.Combine(outDir, "song_" + Path.GetFileNameWithoutExtension(args[a]) + suffix + ".wav");
            long frames = 0;
            using (var w = new BinaryWriter(File.Create(outPath)))
            {
                WriteHeader(w, 0);
                while (seq.IsPlaying)
                {
                    seq.FillMidiEventQueue();
                    synth.GetNext(buffer);
                    foreach (float s in buffer)
                        w.Write((short)(Math.Max(-1f, Math.Min(1f, s * gain)) * 32767));
                    frames += buffer.Length / Channels;
                }
                w.Seek(0, SeekOrigin.Begin);
                WriteHeader(w, frames);
            }
            Console.WriteLine("{0} {1:F1} s", outPath, frames / (double)SampleRate);
        }
        return 0;
    }

    static void WriteHeader(BinaryWriter w, long frames)
    {
        int dataBytes = (int)(frames * Channels * 2);
        w.Write("RIFF".ToCharArray()); w.Write(36 + dataBytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)Channels); w.Write(SampleRate);
        w.Write(SampleRate * Channels * 2); w.Write((short)(Channels * 2)); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(dataBytes);
    }
}
