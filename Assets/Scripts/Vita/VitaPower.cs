// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// GPU speed setting (settings.ini [Video] VitaGpuMode, Advanced Settings "GPU Speed"): the Vita's power configuration mode.
// 0 = A (normal GPU clock), 1 = B (high GPU clock, no Wi-Fi), 2 = C (high GPU clock, limited screen brightness),
// 3 = A with the GPU clock raised by hand.
// The exterior is GPU bound: 25 FPS at the normal clock, 35.6 at the high one. Dungeons/cities are CPU bound (no change).
// Set through the VitaPower plugin (vita/plugins/vitapower); applied at start, on settings save and after a resume.
// The fast modes also raise the ARM (CPU) clock 333 → 444 MHz: the Palace/castles and cities are CPU bound (plugin: patched stub NIDs).
// In game: pause menu dropdown "CPU/GPU speed" steps through the modes, and "Frame Rate" toggles the 30 FPS lock
// (Advanced Settings is only in the setup wizard).
// Mode B is not offered and never applied: it switches Wi-Fi off, and Wi-Fi stayed off after leaving the game (the Vita sends
// no suspend event, and a LiveArea close kills the app, so Restore() below may never run). Mode 3 has the same clocks with Wi-Fi on.
// Leaving the game (suspend, quit) puts the Vita back to mode A at 333 MHz, so other apps get normal power and Wi-Fi; coming back
// reapplies the setting.

#if UNITY_PSP2
using System.Runtime.InteropServices;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;

public class VitaPower : MonoBehaviour
{
#if !UNITY_EDITOR
    [DllImport("VitaPower")] static extern int VitaPower_SetMode(int mode);
    [DllImport("VitaPower")] static extern int VitaPower_SetArmClock(int mhz);
    [DllImport("VitaPower")] static extern int VitaPower_GetArmClock();
    [DllImport("VitaPower")] static extern int VitaPower_SetGpuClock(int mhz);
    [DllImport("VitaPower")] static extern int VitaPower_GetGpuClock();
    [DllImport("__Internal")] static extern int sceKernelExitProcess(int res);
#endif

    /// <summary>Exit Game: Application.Quit does nothing on the Vita, so restore normal power and end the process (back to the LiveArea).</summary>
    public static void Exit()
    {
#if !UNITY_EDITOR
        Restore();
        sceKernelExitProcess(0);
#endif
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        GameObject go = new GameObject("VitaPower");
        DontDestroyOnLoad(go);
        go.AddComponent<VitaPower>();
        Apply();
    }

    public static void Apply()
    {
#if !UNITY_EDITOR
        int mode = DaggerfallUnity.Settings.VitaGpuMode;
        // Mode B switches Wi-Fi off: run it as "Fast" (same clocks, Wi-Fi on)
        if (mode == 1)
            mode = 3;
        Debug.LogFormat("VitaPower: GPU mode {0} result {1:x}", mode, VitaPower_SetMode(mode == 3 ? 0 : mode));
        // Modes B/C set the high GPU clock themselves; A gets it by hand for "Fast", and back to normal for "Normal"
        if (mode == 0 || mode == 3)
            SetGpuClock(mode == 3 ? 166 : 111);
        SetArmClock(mode == 0 ? 333 : 444);
#endif
    }

    // ARM clock in MHz
    public static void SetArmClock(int mhz)
    {
#if !UNITY_EDITOR
        int result = VitaPower_SetArmClock(mhz);
        Debug.LogFormat("VitaPower: ARM clock {0} result {1:x} now {2} MHz", mhz, result, VitaPower_GetArmClock());
#endif
    }

    // GPU clock in MHz
    public static void SetGpuClock(int mhz)
    {
#if !UNITY_EDITOR
        int result = VitaPower_SetGpuClock(mhz);
        Debug.LogFormat("VitaPower: GPU clock {0} result {1:x} now {2} MHz", mhz, result, VitaPower_GetGpuClock());
#endif
    }

    // Settings value per mode, and the order the menus show them in
    public static readonly string[] ModeNames = { "Normal", "Fast, no Wi-Fi", "Fast, dimmer", "Fast" };

    // Menu and popup name of a mode in the game's language (vitaGpuModes, same order as ModeNames)
    public static string ModeName(int mode)
    {
        string[] names = DaggerfallWorkshop.Game.TextManager.Instance.GetLocalizedTextList("vitaGpuModes", TextCollections.Internal, false);
        return names != null && mode < names.Length ? names[mode] : ModeNames[mode];
    }
    public static readonly int[] MenuOrder = { 0, 3, 2 };

    DaggerfallUI registeredWith;

    void Update()
    {
        // DaggerfallUI lives in the game scene, after this object is made
        if (registeredWith || !DaggerfallUI.Instance)
            return;
        registeredWith = DaggerfallUI.Instance;
        registeredWith.RegisterPauseOptionToDropdown("CPU/GPU speed", NextMode);
        registeredWith.RegisterPauseOptionToDropdown(TextManager.Instance.GetText("GameSettings", "vitaFrameLimit"), NextFrameLimit);
    }

    // Toggles the 30 FPS lock (SettingsManager.VitaFrameLimit), as the Advanced Settings slider does
    static void NextFrameLimit()
    {
        SettingsManager settings = DaggerfallUnity.Settings;
        settings.VitaFrameLimit = 1 - settings.VitaFrameLimit;
        settings.SaveSettings();
        QualitySettings.vSyncCount = settings.VitaFrameLimit == 1 ? 2 : settings.VSync ? 1 : 0;
        string[] names = TextManager.Instance.GetLocalizedTextList("vitaFrameLimits");
        DaggerfallUI.Instance.PopupMessage(string.Format("{0}: {1}", TextManager.Instance.GetText("GameSettings", "vitaFrameLimit"), names[settings.VitaFrameLimit]));
    }

    static void NextMode()
    {
        SettingsManager settings = DaggerfallUnity.Settings;
        settings.VitaGpuMode = MenuOrder[(System.Array.IndexOf(MenuOrder, settings.VitaGpuMode) + 1) % MenuOrder.Length];
        settings.SaveSettings();
        Apply();
        DaggerfallUI.Instance.PopupMessage(string.Format(DaggerfallWorkshop.Game.TextManager.Instance.GetLocalizedText("vitaGpuSpeed"), ModeName(settings.VitaGpuMode)));
    }

    // The system may restore its own mode after a suspend; while away the Vita runs at normal power
    void OnApplicationPause(bool paused)
    {
        if (paused)
            Restore();
        else
            Apply();
    }

    void OnApplicationQuit()
    {
        Restore();
    }

    static void Restore()
    {
#if !UNITY_EDITOR
        Debug.LogFormat("VitaPower: leaving, mode A result {0:x}", VitaPower_SetMode(0));
        SetGpuClock(111);
        SetArmClock(333);
#endif
    }
}
#endif
