// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Pre-rendered songs (vita/tools/render_music.sh → Assets/Resources/VitaMusic): streamed MP3 on PSP2, instead of the C# MIDI synth
// (~half a core) and its SoundFont in RAM.

using UnityEditor;
using UnityEngine;

public class VitaMusicImport : AssetPostprocessor
{
    // Bump when the settings below change: Unity re-imports (re-encodes) the songs only when this version changes
    public override uint GetVersion() { return 5; }

    void OnPreprocessAudio()
    {
        if (!assetPath.StartsWith("Assets/Resources/VitaMusic/"))
            return;
        var importer = (AudioImporter)assetImporter;
        var settings = importer.defaultSampleSettings;
        settings.loadType = AudioClipLoadType.Streaming;
        importer.defaultSampleSettings = settings;
        // MP3, decoded on the CPU (a few % of a core). ATRAC9 failed both ways on the 2018 Vita player: Streaming never started
        // (isPlaying stayed false, with or without LoadAudioData) and CompressedInMemory held each song decoded
        //. PSP2 accepts PCM, ADPCM, HEVAG, MP3 and ATRAC9 only: SetOverrideSampleSettings
        // returns false for Vorbis (and VAG/AAC/XMA/GCADPCM) and keeps the old format.
        settings.compressionFormat = AudioCompressionFormat.MP3;
        settings.quality = 0.5f;   // ~160 kbps (1:43 song = 2.1 MB); tune from a listening test
        if (!importer.SetOverrideSampleSettings("PSP2", settings))
            Debug.LogError("VitaMusicImport: PSP2 rejected " + settings.compressionFormat + " for " + assetPath);
    }
}
