// Vita: DateTime.Now reads as 1601-01-01 in our IL2CPP player (save dates showed "Monday, January 1, 1601").
// The system clock from libSceRtc (linked into the player) gives the real local time.
#if UNITY_PSP2
using System;
using System.Runtime.InteropServices;

public static class VitaClock
{
    [StructLayout(LayoutKind.Sequential)]
    struct SceDateTime
    {
        public ushort year, month, day, hour, minute, second;
        public uint microsecond;
    }

#if !UNITY_EDITOR
    [DllImport("__Internal")] static extern int sceRtcGetCurrentClockLocalTime(out SceDateTime time);
#endif

    /// <summary>Local time from the Vita's clock; DateTime.Now if that fails (and in the editor).</summary>
    public static DateTime Now
    {
        get
        {
#if !UNITY_EDITOR
            SceDateTime t;
            if (sceRtcGetCurrentClockLocalTime(out t) == 0 && t.year >= 2000)
            {
                try
                {
                    return new DateTime(t.year, t.month, t.day, t.hour, t.minute, t.second, (int)(t.microsecond / 1000), DateTimeKind.Local);
                }
                catch (ArgumentException) { }
            }
#endif
            return DateTime.Now;
        }
    }
}
#endif
