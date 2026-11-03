// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:
//
// Notes:
//

using UnityEngine;
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using DaggerfallWorkshop.AudioSynthesis.Bank;
using DaggerfallWorkshop.AudioSynthesis.Sequencer;
using DaggerfallWorkshop.AudioSynthesis.Synthesis;
using DaggerfallWorkshop.AudioSynthesis.Midi;
using DaggerfallWorkshop.Utility.AssetInjection;
using DaggerfallWorkshop.Game.UserInterfaceWindows;

namespace DaggerfallWorkshop
{
    [RequireComponent(typeof(AudioSource))]
    public class DaggerfallSongPlayer : MonoBehaviour
    {
        const string sourceFolderName = "SoundFonts";
        const string defaultSoundFontFilename = "TimGM6mb.sf2";
        const int sampleRate = 48000;
#if UNITY_PSP2
        //. 40 is the synth library's own default; VITASYNTH logs the peak active voices to show whether the cap ever bites.
        const int polyphony = 40;
        const int synthDecimation = 2;
        readonly float[] lastSynthFrame = new float[2];
#else
        const int polyphony = 100;
#endif

        [NonSerialized, HideInInspector]
        public bool IsPlaying = false;
        [NonSerialized, HideInInspector]
        public int CurrentTime = 0;
        [NonSerialized, HideInInspector]
        public int EndTime = 0;

        public bool ShowDebugString = false;

        [Range(0.0f, 10.0f)]
        public float Gain = 5.0f;
        private bool IsMuted = false;
        public string SongFolder = "Songs/";
        public SongFiles Song = SongFiles.song_none;

        AudioSource audioSource;
        Synthesizer midiSynthesizer = null;
#if UNITY_PSP2
        static PatchBank sharedBank;
        bool vitaClipPlaying;               // last logged clip state (VITAMUSIC)
        float vitaClipStarted = -10f;       // unscaled time of the last clip Play()
        AudioClip vitaClip;                 // loaded VitaMusic clip (Resources), unloaded on the next song
#endif
        MidiFileSequencer midiSequencer = null;
        string currentMidiName;
        float[] sampleBuffer = new float[0];
        int channels = 0;
        int bufferLength = 0;
        int numBuffers = 0;
#if UNITY_PSP2
        volatile bool playEnabled = false; // read by the audio (and Vita render) thread: volatile so it isn't cached
        // (data abort) in Synthesizer.NoteOn → VoiceManager.GetFreeVoice → LinkedList.RemoveFirst right after a song change
        readonly object synthLock = new object();
#else
        bool playEnabled = false;
#endif
        bool awakeComplete = false;
        float oldGain;

        bool isImported;
        bool isLoading;     

        /// <summary>
        /// Gets peer AudioSource component.
        /// </summary>
        public AudioSource AudioSource
        {
            get { return (audioSource) ? audioSource : GetComponent<AudioSource>(); }
        }

#if UNITY_PSP2
        // Real Vita: Unity's audio update crashes on a playing AudioSource without a clip (AudioManager::ProcessScheduledSources →
        // synth sounds the same over a silent clip. Streamed, so it holds no sample memory.
        static AudioClip silence;

        void Awake()
        {
            audioSource = GetComponent<AudioSource>();
            if (audioSource && audioSource.playOnAwake)
                PlaySilence();
        }

        void PlaySilence()
        {
            if (silence == null)
                silence = AudioClip.Create("silence", int.MaxValue / 2, 2, AudioSettings.outputSampleRate, true, data => { VitaMemory.PinAudioThread(); Array.Clear(data, 0, data.Length); });
            audioSource.clip = silence;
            audioSource.Play();
        }

#endif
        void Start()
        {
#if UNITY_PSP2
            // Synth + SoundFont (~8 MB) only when a song has no pre-rendered clip (Play)
#else
            InitSynth();
#endif

            DaggerfallVidPlayerWindow.OnVideoStart += DaggerfallVidPlayerWindow_OnVideoStart;
            DaggerfallVidPlayerWindow.OnVideoEnd += DaggerfallVidPlayerWindow_OnVideoEnd;
#if UNITY_PSP2
        }

        // Static events would otherwise keep this player (synth + SoundFont) alive after its scene unloads
        void OnDestroy()
        {
            DaggerfallVidPlayerWindow.OnVideoStart -= DaggerfallVidPlayerWindow_OnVideoStart;
            DaggerfallVidPlayerWindow.OnVideoEnd -= DaggerfallVidPlayerWindow_OnVideoEnd;
#endif
        }

        void Update()
        {
            if (!isImported)
            {
                // Update status
                if (midiSequencer != null)
                {
                    IsPlaying = midiSequencer.IsPlaying;
                    CurrentTime = midiSequencer.CurrentTime;
                    EndTime = midiSequencer.EndTime;
                }
#if UNITY_PSP2
                else
                    IsPlaying = false;  // no synth made yet (lazy), and a stopped clip song must not read as playing
#endif
            }
            else
            {
                // Start playing
#if UNITY_PSP2
                // A streamed clip (VitaMusic) stays Unloaded until it plays, so waiting for Loaded never started it
                if (isLoading && (audioSource.clip.loadState == AudioDataLoadState.Loaded || audioSource.clip.loadType == AudioClipLoadType.Streaming))
#else
                if (isLoading && audioSource.clip.loadState == AudioDataLoadState.Loaded)
#endif
                {
                    isLoading = false;
                    audioSource.Play();
#if UNITY_PSP2
                    vitaClipStarted = Time.unscaledTime;
#endif
                }

#if UNITY_PSP2
                if (isLoading && audioSource.clip.loadState == AudioDataLoadState.Failed)
                {
                    isLoading = false;
                    Debug.LogFormat("VITAMUSIC {0} failed to load", audioSource.clip.name);
                }
#endif

                // Update status
#if UNITY_PSP2
                // A streamed clip isn't isPlaying yet right after Play(); SongManager restarted it every frame
                IsPlaying = audioSource.isPlaying || isLoading || Time.unscaledTime - vitaClipStarted < 2f;
#else
                IsPlaying = audioSource.isPlaying || isLoading;
#endif
#if UNITY_PSP2
                if (audioSource.isPlaying != vitaClipPlaying)
                {
                    vitaClipPlaying = audioSource.isPlaying;
                    Debug.LogFormat("VITAMUSIC {0} {1} at {2:F1} s", audioSource.clip.name, vitaClipPlaying ? "playing" : "stopped", audioSource.time);
                }
#endif
                CurrentTime = audioSource.timeSamples;
                EndTime = audioSource.clip.samples;
            }
            audioSource.volume = IsMuted ? 0f : DaggerfallUnity.Settings.MusicVolume;
        }

        void LateUpdate()
        {
            if (!isImported)
            {
#if UNITY_PSP2
                // The synth is made lazily (Play): no sequencer yet = not playing
                if (audioSource.playOnAwake && (midiSequencer == null || !midiSequencer.IsPlaying) && !awakeComplete)
                {
                    Play();
                    awakeComplete = true;
                }
                if (audioSource.loop && (midiSequencer == null || !midiSequencer.IsPlaying))
                {
                    Play();
                }
#else
                if (audioSource.playOnAwake && (midiSequencer != null && !midiSequencer.IsPlaying) && !awakeComplete)
                {
                    Play();
                    awakeComplete = true;
                }
                if (audioSource.loop && !midiSequencer.IsPlaying)
                {
                    Play();
                }
#endif
            }
        }

        // Vita: no OnGUI for this console-only debug overlay; an idle OnGUI still costs ~0.1 ms per frame
#if !UNITY_PSP2
        void OnGUI()
        {
            if (Event.current.type.Equals(EventType.Repaint) && ShowDebugString)
            {
                GUIStyle style = new GUIStyle();
                style.normal.textColor = Color.black;
                string text = GetDebugString();
                GUI.Label(new Rect(10, 50, 800, 24), text, style);
                GUI.Label(new Rect(8, 48, 800, 24), text);
            }
        }
#endif

        public void Play()
        {
            if (Song == SongFiles.song_none)
                return;

            Play(Song);
        }

        /// <summary>
        /// Play current song.
        /// </summary>
        public void Play(SongFiles song)
        {
#if UNITY_PSP2
            if (!audioSource && !(audioSource = GetComponent<AudioSource>()))
                return;
#else
            if (!InitSynth())
                return;
#endif

            // Stop if playing another song
            Stop();

            // Import custom song
            AudioClip clip;
#if UNITY_PSP2
            // Pre-rendered song (vita/tools/render_music.sh, ATRAC9 streamed): no C# synth on the audio thread. Rendered per player Gain.
            clip = Resources.Load<AudioClip>("VitaMusic/" + song + "_g" + (int)Gain);
            // One song's audio in memory at a time. Not Resources.UnloadAsset: on a clip that never loaded it crashed in
            // FMOD::Sample::release
            if (vitaClip && vitaClip != clip && vitaClip.loadState == AudioDataLoadState.Loaded)
                vitaClip.UnloadAudioData();
            vitaClip = clip;
            if (clip && clip.loadState == AudioDataLoadState.Unloaded)
                clip.LoadAudioData();               // Resources.Load left it Unloaded, so Update never started it
            if (isImported = clip != null || SoundReplacement.TryImportSong(song, out clip))
#else
            if (isImported = SoundReplacement.TryImportSong(song, out clip))
#endif
            {
                Song = song;
#if UNITY_PSP2
                Debug.LogFormat("VITAMUSIC {0} gain {1}: {2} {3} {4}", song, Gain, clip.name, clip.loadType, clip.loadState);
#endif
                audioSource.clip = clip;
                isLoading = true;
                return;
            }

#if UNITY_PSP2
            if (!InitSynth())
                return;

#endif
            // Load song data
            string filename = EnumToFilename(song);
            byte[] songData = LoadSong(filename);
            if (songData == null)
                return;

            // Create song
            MidiFile midiFile = new MidiFile(new MyMemoryFile(songData, filename));
#if UNITY_PSP2
            lock (synthLock)
            {
                if (midiSequencer.LoadMidi(midiFile))
                {
                    midiSequencer.Play();
                    currentMidiName = filename;
                    playEnabled = true;
                    IsPlaying = true;
                }
            }
#else
            if (midiSequencer.LoadMidi(midiFile))
            {
                midiSequencer.Play();
                currentMidiName = filename;
                playEnabled = true;
                IsPlaying = true;
            }
#endif
        }

        /// <summary>
        /// Stop playing song.
        /// </summary>
        public void Stop()
        {
#if UNITY_PSP2
            // Play calls Stop first: InitSynth here loaded the synth + SoundFont (~13 MB heap peak) for pre-rendered songs too
            if (!audioSource && !(audioSource = GetComponent<AudioSource>()))
                return;
#else
            if (!InitSynth())
                return;
#endif

            // Reset audiosource clip
            if (isImported)
            {
                isImported = false;
                audioSource.Stop();
#if UNITY_PSP2
                PlaySilence();
#else
                audioSource.clip = null;
                audioSource.Play();
#endif
            }

            // Stop if playing a song
#if UNITY_PSP2
            lock (synthLock)
            {
                if (midiSequencer != null && midiSequencer.IsPlaying)
                {
                    playEnabled = false;
                    midiSequencer.Stop();
                    midiSynthesizer.NoteOffAll(true);
                    midiSynthesizer.ResetSynthControls();
                    midiSynthesizer.ResetPrograms();
                }
            }
#else
            if (midiSequencer.IsPlaying)
            {
                midiSequencer.Stop();
                midiSynthesizer.NoteOffAll(true);
                midiSynthesizer.ResetSynthControls();
                midiSynthesizer.ResetPrograms();
                playEnabled = false;
            }
#endif
        }

        #region Private Methods

        private bool InitSynth()
        {
            // Get peer AudioSource
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
            {
                DaggerfallUnity.LogMessage("DaggerfallSongPlayer: Could not find AudioSource component.");
                return false;
            }

            // Create synthesizer and load bank
            if (midiSynthesizer == null)
            {
                // Get number of channels
                if (AudioSettings.driverCapabilities.ToString() == "Mono")
                    channels = 1;
                else
                    channels = 2;

                // Create synth
                AudioSettings.GetDSPBufferSize(out bufferLength, out numBuffers);
#if UNITY_PSP2
                midiSynthesizer = new Synthesizer(sampleRate / synthDecimation, channels, bufferLength / numBuffers / synthDecimation, numBuffers, polyphony);
#else
                midiSynthesizer = new Synthesizer(sampleRate, channels, bufferLength / numBuffers, numBuffers, polyphony);
#endif

#if UNITY_PSP2
                // All song players share one SoundFont: each copy is ~7 MB of C# heap.
                if (sharedBank != null)
                {
                    midiSynthesizer.LoadBank(sharedBank);
                    midiSynthesizer.ResetSynthControls();
                }
                else
                {
#endif
                // Load bank data
                string filename = DaggerfallUnity.Settings.SoundFont;
                byte[] bankData = LoadBank(filename);
                if (bankData == null)
                {
                    // Attempt to fallback to default internal soundfont
                    bankData = LoadDefaultSoundFont();
                    filename = defaultSoundFontFilename;
                    Debug.LogFormat("Using default SoundFont {0}", defaultSoundFontFilename);
                }
                else
                {
                    Debug.LogFormat("Trying custom SoundFont {0}", filename);
                }

                // Assign to synth
                if (bankData == null)
                    return false;
                else
                {
#if UNITY_PSP2
                    midiSynthesizer.LoadBank(sharedBank = new PatchBank(new MyMemoryFile(bankData, filename)));
#else
                    midiSynthesizer.LoadBank(new MyMemoryFile(bankData, filename));
#endif
                    midiSynthesizer.ResetSynthControls(); // Need to do this for bank to load properly, don't know why
#if UNITY_PSP2
                }
#endif
                }
            }

            // Create sequencer
            if (midiSequencer == null)
                midiSequencer = new MidiFileSequencer(midiSynthesizer);

            // Check init
            if (midiSynthesizer == null || midiSequencer == null)
            {
                DaggerfallUnity.LogMessage("DaggerfallSongPlayer: Failed to init synth.");
                return false;
            }

            return true;
        }

        private string EnumToFilename(SongFiles song)
        {
            string enumName = song.ToString();
            return enumName.Remove(0, "song_".Length) + ".mid";
        }

        private byte[] LoadBank(string filename)
        {
            // Do nothing if no filename set
            if (string.IsNullOrEmpty(filename))
                return null;

            // Check file exists
            string path = Path.Combine(Application.streamingAssetsPath, sourceFolderName);
            string filePath = Path.Combine(path, filename);
            if (!File.Exists(filePath))
            {
                // Fallback to default sound font
                Debug.LogFormat("Could not find file '{0}', falling back to default soundfont {1}.", filePath, defaultSoundFontFilename);
                return null;
            }

            // Load data
            return File.ReadAllBytes(filePath);
        }

        private byte[] LoadDefaultSoundFont()
        {
            TextAsset asset = Resources.Load<TextAsset>(defaultSoundFontFilename);
            if (asset != null)
            {
#if UNITY_PSP2
                // .bytes is a managed copy: drop the native asset now (6 MB) instead of at the next UnloadUnusedAssets
                byte[] bytes = asset.bytes;
                Resources.UnloadAsset(asset);
                return bytes;
#else
                return asset.bytes;
#endif
            }

            DaggerfallUnity.LogMessage(string.Format("DaggerfallSongPlayer: Bank file '{0}' not found.", defaultSoundFontFilename));

            return null;
        }

        private byte[] LoadSong(string filename)
        {
            // Get custom midi song
            byte[] songBytes;
            if (SoundReplacement.TryImportMidiSong(filename, out songBytes))
                return songBytes;

            // Get Daggerfal song
            TextAsset asset = Resources.Load<TextAsset>(Path.Combine(SongFolder, filename));
            if (asset != null)
            {
                return asset.bytes;
            }

            DaggerfallUnity.LogMessage(string.Format("DaggerfallSongPlayer: Song file '{0}' not found.", filename));

            return null;
        }

        private string GetDebugString()
        {
            if (midiSequencer == null)
                return "Sequencer not ready.";
            if (midiSynthesizer == null)
                return "Synthesizer not ready.";

            string final;
            if (isImported)
            {
                if (isLoading)
                    final = string.Format("Loading song '{0}'", Song);
                else
                    final = string.Format("Playing song '{0}' at position {1}/{2}", Song, audioSource.timeSamples, audioSource.clip.samples);
            }
            else if (midiSequencer.IsPlaying)
                final = string.Format("Playing song '{0}' at position {1}/{2}", currentMidiName, midiSequencer.CurrentTime, midiSequencer.EndTime);
            else
                final = string.Format("Song '{0}' ready. Not playing.", currentMidiName);

            return final;
        }

        private void DaggerfallVidPlayerWindow_OnVideoStart()
        {
            // Mute music while video is playing
            oldGain = Gain;
            Gain = 0;
            IsMuted = true;
        }

        private void DaggerfallVidPlayerWindow_OnVideoEnd()
        {
            // Restore music to previous level
            Gain = oldGain;
            IsMuted = false;
        }

        #endregion

        #region Audio Filter

#if UNITY_PSP2
        // Vita: synthesize in the audio callback at half rate (synthDecimation), as on PC. A render thread with a ring buffer (runs 50-54)
        // can't work here: a managed System.Threading.Thread hung in Thread.Sleep after ~1.4 s (runs 50-53), and once it did render, the
        // game froze at the next GC. Leftover output frames carry over to the next callback when the sizes don't divide.
        float[] renderOut = new float[0];
        int renderOutPos, renderOutCount;

        // Probes: synth time per rendered chunk vs the chunk's play time
        double synthMs, synthMaxMs;
        int synthChunks, synthOverBudget, synthMaxVoices;
        // Does Unity hand OnAudioFilterRead a new array per callback (garbage the synth can't avoid)? Counted per log line
        float[] lastFilterData;
        int filterCalls, newFilterArrays;

        void LogSynthTime(long start, int samples)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double budget = samples * 1000.0 / sampleRate; // output rate (VITAAUDIO); no Unity API off the main thread
            synthMs += ms;
            synthMaxMs = Math.Max(synthMaxMs, ms);
            synthChunks++;
            if (ms > budget)
                synthOverBudget++;
            synthMaxVoices = Math.Max(synthMaxVoices, midiSynthesizer.ActiveVoices);
            if (synthChunks < 500)
                return;
            Debug.Log(string.Format("VITASYNTH song={0} chunks={1} avg={2:F2}ms max={3:F2}ms budget={4:F2}ms over={5} maxVoices={6}/{7} filterCalls={8} newArrays={9} len={10}",
                Song, synthChunks, synthMs / synthChunks, synthMaxMs, budget, synthOverBudget, synthMaxVoices, polyphony,
                filterCalls, newFilterArrays, lastFilterData != null ? lastFilterData.Length : 0)); // no Unity object API (name)
            synthMs = synthMaxMs = 0;
            synthChunks = synthOverBudget = synthMaxVoices = 0;
            filterCalls = newFilterArrays = 0;
        }

        // Render one synth chunk into renderOut: each synth frame becomes two output frames, the midpoint from the previous frame, then the frame itself
        void RenderChunk()
        {
            long synthStart = VitaMemory.Probes ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (sampleBuffer.Length != midiSynthesizer.WorkingBufferSize)
                sampleBuffer = new float[midiSynthesizer.WorkingBufferSize];
            bool locked = false;
            System.Threading.Monitor.TryEnter(synthLock, ref locked);
            if (!locked)
                Array.Clear(sampleBuffer, 0, sampleBuffer.Length);  // Play/Stop hold the lock: one chunk of silence, the audio thread never waits
            else
            {
                try
                {
                    midiSequencer.FillMidiEventQueue();
                    midiSynthesizer.GetNext(sampleBuffer);
                }
                finally
                {
                    System.Threading.Monitor.Exit(synthLock);
                }
            }

            int count = sampleBuffer.Length * synthDecimation;
            if (renderOut.Length != count)
                renderOut = new float[count];
            for (int s = 0, frames = sampleBuffer.Length / channels; s < frames; s++)
            {
                for (int c = 0; c < channels; c++)
                {
                    float sample = sampleBuffer[s * channels + c];
                    renderOut[(2 * s) * channels + c] = (lastSynthFrame[c] + sample) * 0.5f * Gain;
                    renderOut[(2 * s + 1) * channels + c] = sample * Gain;
                    lastSynthFrame[c] = sample;
                }
            }
            renderOutPos = 0;
            renderOutCount = count;
            if (VitaMemory.Probes)
                LogSynthTime(synthStart, count / channels);
        }

#endif
        // Called when audio filter needs more sound data
        void OnAudioFilterRead(float[] data, int channels)
        {
#if UNITY_PSP2
            VitaMemory.PinAudioThread();
            if (!playEnabled || midiSynthesizer == null || midiSequencer == null)
#else
            // Do nothing if play not enabled
            // This flag is raised/lowered when user starts/stops play
            // Helps avoids thread finding synth in state of shutting down
            if (!playEnabled)
#endif
                return;
#if !UNITY_PSP2

            // Must have synth and seq
            if (midiSynthesizer == null || midiSequencer == null)
                return;

            // Sample buffer size must match working buffer size
            if (sampleBuffer.Length != midiSynthesizer.WorkingBufferSize)
                sampleBuffer = new float[midiSynthesizer.WorkingBufferSize];
#endif

            try
            {
#if UNITY_PSP2
                if (!midiSequencer.IsMidiLoaded || !midiSequencer.IsPlaying)
#else
                // Update sequencing - must be playing a song
                if (midiSequencer.IsMidiLoaded && midiSequencer.IsPlaying)
#endif
                {
#if UNITY_PSP2
                    renderOutPos = renderOutCount = 0;
                    return;
                }
                if (VitaMemory.Probes)
                {
                    filterCalls++;
                    if (!ReferenceEquals(data, lastFilterData))
                        newFilterArrays++;
                    lastFilterData = data;
                }
                for (int i = 0; i < data.Length; )
                {
                    if (renderOutPos >= renderOutCount)
                        RenderChunk();
                    int n = Math.Min(data.Length - i, renderOutCount - renderOutPos);
                    Array.Copy(renderOut, renderOutPos, data, i, n);
                    renderOutPos += n;
                    i += n;
#else
                    midiSequencer.FillMidiEventQueue();
                    midiSynthesizer.GetNext(sampleBuffer);
                    for (int i = 0; i < data.Length; i++)
                    {
                        data[i] = sampleBuffer[i] * Gain;
                    }
#endif
                }
            }
            catch (Exception)
            {
#if UNITY_PSP2
                // Synth starting up or shutting down (see the PC version below)
#else
                // Will sometimes drop here if Unity tries to feed audio filter
                // from another thread while synth is starting up or shutting down
                // Just nom the exception
#endif
            }
        }

        #endregion

        #region Interface Implementation

        public class MyMemoryFile : AudioSynthesis.IResource
        {
            private byte[] file;
            private string fileName;
            public MyMemoryFile(byte[] file, string fileName)
            {
                this.file = file;
                this.fileName = fileName;
            }
            public string GetName() { return fileName; }
            public bool DeleteAllowed() { return false; }
            public bool ReadAllowed() { return true; }
            public bool WriteAllowed() { return false; }
            public void DeleteResource() { return; }
            public Stream OpenResourceForRead() { return new MemoryStream(file); }
            public Stream OpenResourceForWrite() { return null; }
        }

        #endregion

        #region Static Song Arrays

        /// <summary>
        /// Just the GM songs.
        /// </summary>
        public static SongFiles[] Songs_GM = new SongFiles[]
        {
            SongFiles.song_02,
            SongFiles.song_03,
            SongFiles.song_04,
            SongFiles.song_05,
            SongFiles.song_06,
            SongFiles.song_07,
            SongFiles.song_08,
            SongFiles.song_09,
            SongFiles.song_10,
            SongFiles.song_11,
            SongFiles.song_12,
            SongFiles.song_13,
            SongFiles.song_15,
            SongFiles.song_16,
            SongFiles.song_17,
            SongFiles.song_18,
            SongFiles.song_20,
            SongFiles.song_21,
            SongFiles.song_22,
            SongFiles.song_23,
            SongFiles.song_25,
            SongFiles.song_28,
            SongFiles.song_29,
            SongFiles.song_30,
            SongFiles.song_d1,
            SongFiles.song_d10,
            SongFiles.song_d2,
            SongFiles.song_d3,
            SongFiles.song_d4,
            SongFiles.song_d5,
            SongFiles.song_d6,
            SongFiles.song_d7,
            SongFiles.song_d8,
            SongFiles.song_d9,
            SongFiles.song_dungeon,
            SongFiles.song_dungeon5,
            SongFiles.song_dungeon6,
            SongFiles.song_dungeon7,
            SongFiles.song_dungeon8,
            SongFiles.song_dungeon9,
            SongFiles.song_folk1,
            SongFiles.song_folk2,
            SongFiles.song_folk3,
            SongFiles.song_gbad,
            SongFiles.song_gcurse,
            SongFiles.song_gday___d,
            SongFiles.song_gdngn10,
            SongFiles.song_gdngn11,
            SongFiles.song_gdungn4,
            SongFiles.song_gdungn9,
            SongFiles.song_geerie,
            SongFiles.song_ggood,
            SongFiles.song_gmage_3,
            SongFiles.song_gneut,
            SongFiles.song_gpalac,
            SongFiles.song_gruins,
            SongFiles.song_gshop,
            SongFiles.song_gsneak2,
            SongFiles.song_gsnow__b,
            SongFiles.song_gsunny2,
            SongFiles.song_magic_2,
            SongFiles.song_overcast,
            SongFiles.song_overlong,
            SongFiles.song_oversnow,
            SongFiles.song_raining,
            SongFiles.song_sneaking,
            SongFiles.song_sneakng2,
            SongFiles.song_snowing,
            SongFiles.song_square_2,
            SongFiles.song_sunnyday,
            SongFiles.song_swimming,
            SongFiles.song_tavern,
        };

        #endregion
    }
}