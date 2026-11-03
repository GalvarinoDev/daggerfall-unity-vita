// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2024 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: kaboissonneault
// Contributors:    
// 
// Notes:
//

//#define SEPARATE_DEV_PERSISTENT_PATH

using System;
using System.IO;
using UnityEngine;

public static class DaggerfallUnityApplication
{
    static string persistentDataPath;
    private static bool? isPortableInstall;

    public static bool IsPortableInstall
    {
        get
        {
            if (isPortableInstall == null)
            {
                isPortableInstall = !Application.isEditor && File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Portable.txt"));
            }

            return isPortableInstall.Value;
        }
    }

    public static string PersistentDataPath
    {
        get
        {
            if (persistentDataPath == null)
            {
                InitializePersistentPath();
            }

            return persistentDataPath;
        }
    }

    private static void InitializePersistentPath()
    {
#if UNITY_EDITOR && SEPARATE_DEV_PERSISTENT_PATH
        persistentDataPath = String.Concat(Application.persistentDataPath, ".devenv");
        Directory.CreateDirectory(persistentDataPath);
#elif UNITY_PSP2 && !UNITY_EDITOR
        // app0: is read-only; keep settings, keybinds and saves beside the game files (SettingsManager's MyDaggerfallPath default)
        persistentDataPath = "ux0:/data/DaggerfallUnity/UserData";
        Directory.CreateDirectory(persistentDataPath);
#else
        if (IsPortableInstall)
        {
            persistentDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PortableAppdata");
            Directory.CreateDirectory(persistentDataPath);
        }
        else
        {
            persistentDataPath = Application.persistentDataPath;
        }
#endif
    }

#if UNITY_PSP2 // SubsystemRegistration is Unity 2019.2+
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#else
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
#endif
    static void SubsystemInit()
    {
        if (persistentDataPath == null)
        {
            InitializePersistentPath();
        }
        InitLog();
    }


    public class LogHandler : ILogHandler, IDisposable
    {
        private StreamWriter streamWriter;
#if UNITY_PSP2
        // Seconds since launch on every line, to see where the boot goes (Time.realtimeSinceStartup is main-thread only, so a
        // Stopwatch from here plus the launch offset read once on the main thread)
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly float launchOffset = Time.realtimeSinceStartup;
#endif

        public LogHandler()
        {
            string filePath = Path.Combine(persistentDataPath, "Player.log");

            string errorMessage = null;
            try
            {
                if(File.Exists(filePath))
                {
                    string prevPath = Path.Combine(persistentDataPath, "Player-prev.log");
#if UNITY_PSP2
                    VitaFile.Delete(prevPath);
#else
                    File.Delete(prevPath);
#endif
                    File.Move(filePath, prevPath);
                }
            }
            catch(Exception e)
            {
                errorMessage = $"Could not preserve previous log: {e.Message}";
            }
                        
            streamWriter = File.CreateText(filePath);
            streamWriter.AutoFlush = true;

            if(!string.IsNullOrEmpty(errorMessage))
            {
                streamWriter.WriteLine(errorMessage);
            }
        }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
#if UNITY_PSP2
            FlushRepeats();
            streamWriter.WriteLine(string.Format("{0:F1} [Exception] ", launchOffset + clock.Elapsed.TotalSeconds) + exception.ToString());
#else
            streamWriter.WriteLine(exception.ToString());
#endif
        }

#if UNITY_PSP2
        // Unhandled exceptions (a coroutine or Update that throws) never reach LogException on the Vita release player: Unity reports them
        // on its own path, and nothing showed in Player.log or the TTY. That path raises logMessageReceivedThreaded, as do
        // Unity's own native errors and asserts (none of which ever reached this handler either).
        // Flood guard: a log line costs 7-14 ms on the card, so the same message again only counts, with a "repeated" line every 5 s.
        string lastUnityMessage;
        int lastUnityRepeats;
        double lastUnityFlush;

        // Pending repeat count first, so the next line of any kind closes a flood
        void FlushRepeats()
        {
            if (lastUnityRepeats == 0)
                return;
            lock (streamWriter)
            {
                streamWriter.WriteLine(string.Format("{0:F1} [Unhandled] repeated x{1}: {2}", launchOffset + clock.Elapsed.TotalSeconds, lastUnityRepeats, lastUnityMessage));
                lastUnityRepeats = 0;   // message kept: a flood still going on keeps counting instead of printing its stack again
            }
        }

        public void OnUnityMessage(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert)
                return;
            lock (streamWriter)
            {
                double now = launchOffset + clock.Elapsed.TotalSeconds;
                if (message == lastUnityMessage)
                {
                    lastUnityRepeats++;
                    if (now - lastUnityFlush >= 5)
                    {
                        streamWriter.WriteLine(string.Format("{0:F1} [Unhandled] repeated x{1}: {2}", now, lastUnityRepeats, message));
                        lastUnityRepeats = 0;
                        lastUnityFlush = now;
                    }
                    return;
                }
                FlushRepeats();
                lastUnityMessage = message;
                lastUnityRepeats = 0;
                lastUnityFlush = now;
                string tag = type == LogType.Exception ? "[Unhandled] " : type == LogType.Error ? "[Unity error] " : "[Unity assert] ";
                streamWriter.WriteLine(string.Format("{0:F1} ", now) + tag + message + (string.IsNullOrEmpty(stackTrace) ? "" : "\n" + stackTrace));
            }
        }
#endif

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            string prefix = "";
            switch(logType)
            {
                case LogType.Error:
                    prefix = "[Error] ";
                    break;

                case LogType.Warning:
                    prefix = "[Warning] ";
                    break;

                case LogType.Assert:
                    prefix = "[Assert] ";
                    break;

                case LogType.Exception:
                    prefix = "[Exception] ";
                    break;
            }

#if UNITY_PSP2
            FlushRepeats();
            streamWriter.WriteLine(string.Format("{0:F1} ", launchOffset + clock.Elapsed.TotalSeconds) + prefix + string.Format(format, args));
#else
            streamWriter.WriteLine(prefix + string.Format(format, args));
#endif
        }

        public void Dispose()
        {
            streamWriter.Close();
        }
    }

    static void InitLog()
    {
        if (Application.isPlaying && Application.installMode != ApplicationInstallMode.Editor)
        {
#if UNITY_PSP2
            LogHandler handler = new LogHandler();
            Debug.unityLogger.logHandler = handler;
            Application.logMessageReceivedThreaded += handler.OnUnityMessage;
#else
            Debug.unityLogger.logHandler = new LogHandler();
#endif
        }
    }
}
