// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Memory-reset relaunch. The IL2CPP heap never shrinks and Unity's native heaps keep their peak, so a long session (walking nature and big
// cities) ends with ~20 MB free main where a fresh boot has ~90. When free main runs low, this writes a hidden save (RELAUNCH.vita: not a
// SAVEn file, so the save list never shows it) and a marker, then starts the game again from its own package (VitaPower.Relaunch,
// sceAppMgrLoadExec). The next boot finds the marker in StartGameBehaviour.Start, skips the title and loads the hidden save; the marker
// is deleted first, so a load that fails can't loop. Two levels: low = after a door, dungeon, fast travel or load has finished;
// critical = any time in play, checked every few seconds. Either only when a normal save would be allowed (HUD on top, no load,
// nothing building, no Travel Options journey, saving not prevented), and not in the first minutes after a relaunch.
// Not in the save, so it changes across a relaunch: music position, civilians and ambient spawns, HUD messages. Camera pitch and the weather
// go into the marker and are set again after the load (the save keeps the weather too; the marker copy is a check).
// The restart looks like one load (2026-11-16): the loading picture shown before it is named in the marker, so the next boot draws the same
// picture from its first frame (VitaBootSplash) through the load (VitaLoadingWindow), with no boot music (vita/bootmusic checks the
// marker), no launcher (SceneControl) and no title song.
// A Travel Options journey has no doors and streams ground fast: below the low level it is stopped, the relaunch follows at the first
// allowed point, and after the reload the same journey starts again at the same speed (relaunch.journey next to the marker).

#if UNITY_PSP2
using System.Collections;
using System.IO;
using UnityEngine;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Weather;
using DaggerfallWorkshop.Utility;

public class VitaRelaunch : MonoBehaviour
{
    // From a 3.4 h walking soak (2026-11-13): free main never went below 42 MB on arriving in a town and a big-city walk cost up to
    // 25 MB (lowest 28 MB); one crash came with 20-23 MB still free (a single bigger allocation failed), mesh/texture allocations fail near
    // 10 MB. Low = after a door or load, critical = any time in play.
    public static long LowFreeMain = 30L << 20;
    public static long CriticalFreeMain = 22L << 20;
    // Video memory: when CDRAM runs out Unity moves textures to main memory, and when that fails render targets and shader programs can't
    // be made (a 2026-11-16 crash: a clear on the render thread with no uniform buffer, after free CDRAM had been at 6.7 MB). Trial values,
    // to be set from a measured run.
    public static long LowFreeCdram = 8L << 20;
    public static long CriticalFreeCdram = 4L << 20;
    const float CriticalCheckSeconds = 5f;
    const float QuietAfterBootSeconds = 300f;

    public static bool Off;              // test bot
    public static bool ForceNext;        // test bot: relaunch at the next allowed point whatever the memory
    public static bool Relaunched { get; private set; }     // this boot came from a relaunch
    static bool bootLoadPending;

    static VitaRelaunch instance;
    bool checkPending;
    bool busy;
    float nextCriticalCheck;
    bool journeyStopped;
    string journey;                 // the stopped journey, written with the marker
    static string pendingJourney;   // read back at boot, started again after the load
    float nextJourneyCheck;

    static string JourneyPath { get { return Path.Combine(DaggerfallUnityApplication.PersistentDataPath, "relaunch.journey"); } }
    const float NoticeSeconds = 2.5f;     // the notice stays up at least this long (readable before the screen goes dark)
    static string ScreenPath { get { return Path.Combine(DaggerfallUnityApplication.PersistentDataPath, "relaunch.screen"); } }
    static string MarkerPath { get { return Path.Combine(DaggerfallUnityApplication.PersistentDataPath, "relaunch.marker"); } }

    // Marker lines: why, loading picture (VitaLoadingWindow list line, empty = none), weather type, camera pitch
    static string[] marker;
    static bool markerRead;
    static bool restoreView;
    static int restoreWeather;
    static float restorePitch;

    static string[] ReadMarker()
    {
        if (!markerRead)
        {
            markerRead = true;
            try
            {
                if (File.Exists(MarkerPath))
                    marker = File.ReadAllText(MarkerPath).Split('\n');
            }
            catch (System.Exception e)
            {
                Debug.LogError("VITARELAUNCH marker: " + e.Message);
            }
        }
        return marker;
    }

    /// <summary>Boot scenes: this boot is a relaunch (the marker is there; StartGameBehaviour takes it later).</summary>
    public static bool Pending { get { return ReadMarker() != null; } }

    /// <summary>Boot scenes: the loading picture the relaunch showed (a VitaLoadingWindow list line), or null.</summary>
    public static string PendingPicture
    {
        get
        {
            string[] m = ReadMarker();
            return m != null && m.Length > 1 && m[1].Length > 0 ? m[1] : null;
        }
    }
    public static string SavePath { get { return Path.Combine(SaveLoadManager.Instance.UnitySavePath, "RELAUNCH" + VitaSaveIO.Extension); } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        if (instance)
            return;
        GameObject go = new GameObject("VitaRelaunch");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<VitaRelaunch>();
    }

    void OnEnable()
    {
        PlayerEnterExit.OnTransitionInterior += Transition;
        PlayerEnterExit.OnTransitionExterior += Transition;
        PlayerEnterExit.OnTransitionDungeonInterior += Transition;
        PlayerEnterExit.OnTransitionDungeonExterior += Transition;
        StreamingWorld.OnTeleportToCoordinates += Teleport;
        SaveLoadManager.OnLoad += Loaded;
    }

    void OnDisable()
    {
        PlayerEnterExit.OnTransitionInterior -= Transition;
        PlayerEnterExit.OnTransitionExterior -= Transition;
        PlayerEnterExit.OnTransitionDungeonInterior -= Transition;
        PlayerEnterExit.OnTransitionDungeonExterior -= Transition;
        StreamingWorld.OnTeleportToCoordinates -= Teleport;
        SaveLoadManager.OnLoad -= Loaded;
    }

    void Transition(PlayerEnterExit.TransitionEventArgs args) { checkPending = true; }
    void Teleport(DFPosition worldPos) { checkPending = true; }
    void Loaded(SaveData_v1 saveData)
    {
        checkPending = true;
        if (restoreView)
            StartCoroutine(RestoreView());
#if VITA_TRAVELOPTIONS
        if (pendingJourney != null)
            StartCoroutine(ResumeJourney());
#endif
    }

    /// <summary>StartGameBehaviour.Start: true (and the marker gone) when this boot should load the hidden save instead of the title.</summary>
    public static bool TakeMarker()
    {
        try
        {
            string[] m = ReadMarker();
            if (m == null)
                return false;
            File.Delete(MarkerPath);
            marker = null;
            if (m.Length > 3)
            {
                VitaLoadingWindow.NextPicture = m[1].Length > 0 ? m[1] : null;
                restoreView = int.TryParse(m[2], out restoreWeather) &&
                    float.TryParse(m[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out restorePitch);
            }
            bool ok = File.Exists(SavePath);
            Debug.Log("VITARELAUNCH boot: marker found, hidden save " + (ok ? "present" : "missing (title instead)"));
            Relaunched = bootLoadPending = ok;
            if (File.Exists(JourneyPath))
            {
                pendingJourney = ok ? File.ReadAllText(JourneyPath) : null;
                File.Delete(JourneyPath);
            }
            return ok;
        }
        catch (System.Exception e)
        {
            Debug.LogError("VITARELAUNCH marker: " + e.Message);
            return false;
        }
    }

    /// <summary>StartGameBehaviour.LoadDaggerfallUnitySave: loads the hidden save if this boot is a relaunch.</summary>
    public static bool TryBootLoad()
    {
        if (!bootLoadPending)
            return false;
        bootLoadPending = false;
        SaveLoadManager.Instance.VitaLoadFile(SavePath);
        return true;
    }

    float nextBlockLog;
    void Update()
    {
        if (busy || Off || Time.realtimeSinceStartup < (Relaunched ? QuietAfterBootSeconds : 0f))
            return;
#if VITA_TRAVELOPTIONS
        TravelOptions.TravelOptionsMod travel = TravelOptions.TravelOptionsMod.Instance;
        if (travel != null && travel.VitaTravelling && Time.realtimeSinceStartup >= nextJourneyCheck)
        {
            nextJourneyCheck = Time.realtimeSinceStartup + CriticalCheckSeconds;
            if (ForceNext || VitaMemory.FreeMain() < LowFreeMain)
            {
                journey = travel.VitaStopJourney();
                checkPending = journeyStopped = true;
                Debug.LogFormat("VITARELAUNCH journey stopped ({0}): {1}", journey ?? "map coordinates, not resumed", VitaMemory.FreeMem());
            }
        }
#endif
        if (!CanSave())
        {
            if (ForceNext && Time.realtimeSinceStartup >= nextBlockLog)    // test bot: say what holds a forced relaunch back
            {
                nextBlockLog = Time.realtimeSinceStartup + 10f;
                Debug.Log("VITARELAUNCH waiting: " + blocked);
            }
            return;
        }
        long free = VitaMemory.FreeMain();
        if (checkPending || ForceNext)     // forced: no need to wait for a transition (a new game starts without one)
        {
            checkPending = false;
            long cdram = VitaMemory.FreeCdram();
            if (ForceNext || journeyStopped || free < LowFreeMain || cdram < LowFreeCdram)
            {
                StartCoroutine(Relaunch(ForceNext ? "forced" : journeyStopped ? "journey" : free < LowFreeMain ? "low" : "low cdram"));
                return;
            }
        }
        if (Time.realtimeSinceStartup >= nextCriticalCheck)
        {
            nextCriticalCheck = Time.realtimeSinceStartup + CriticalCheckSeconds;
            if (free < CriticalFreeMain)
                StartCoroutine(Relaunch("critical"));
            else if (VitaMemory.FreeCdram() < CriticalFreeCdram)
                StartCoroutine(Relaunch("critical cdram"));
        }
    }

    static bool CanSave()
    {
        if (!GameManager.HasInstance)       // boot scenes: GameManager.Instance logs an exception per call there
            return Blocked("no game manager");
        GameManager gm = GameManager.Instance;
        if (!gm.IsPlayingGame() || !(DaggerfallUI.UIManager.TopWindow is DaggerfallHUD))
            return Blocked(!gm.IsPlayingGame() ? "not playing" : "top window " + DaggerfallUI.UIManager.TopWindow);
        SaveLoadManager slm = SaveLoadManager.Instance;
        if (!slm || slm.LoadInProgress || slm.IsSavingPrevented)
            return Blocked(!slm ? "no save manager" : slm.LoadInProgress ? "loading" : "saving prevented");
        // Outside only: a new game in the start dungeon leaves its RandomStartMarker reposition pending until the player leaves, so
        // VitaIsBuilding reads true for the whole dungeon
        bool building = !gm.PlayerEnterExit.IsPlayerInside && gm.StreamingWorld.VitaIsBuilding;
        if (building || gm.StreamingWorld.VitaTravelling || gm.PlayerEntity.CurrentHealth <= 0 ||
            gm.PlayerEnterExit.VitaDoorFading)
            return Blocked(building ? "building" : gm.StreamingWorld.VitaTravelling ? "travelling" :
                gm.PlayerEnterExit.VitaDoorFading ? "door fade" : "dead");
#if VITA_TRAVELOPTIONS
        if (TravelOptions.TravelOptionsMod.Instance != null && TravelOptions.TravelOptionsMod.Instance.VitaTravelling)
            return Blocked("Travel Options journey");
#endif
#if VITA_DRAGONRIDER
        // The save keeps "mounted, flight mode" but not the altitude: dragon1's reload flew on at ground level. Flights stayed far above
        // the thresholds (lowest free main 67 MB in 40 min), so wait for the landing.
        if (DragonRider.DragonRider.IsFlightSimMode)
            return Blocked("dragon flight");
#endif
        return true;
    }

#if VITA_TRAVELOPTIONS
    // After a relaunch's load: once the HUD is up and the ground built, the stopped journey goes on (up to 2 min, then it stays paused)
    IEnumerator ResumeJourney()
    {
        string resume = pendingJourney;
        pendingJourney = null;
        // CanSave is true the moment the save's data is in, while the load still finishes (its loading window closes later and takes a
        // travel window with it: journey2 resumed and was interrupted 0.1 s later), so wait for 3 s of CanSave in a row
        float start = Time.realtimeSinceStartup, readySince = -1;
        while (Time.realtimeSinceStartup - start < 120f)
        {
            if (!CanSave())
                readySince = -1;
            else if (readySince < 0)
                readySince = Time.realtimeSinceStartup;
            else if (Time.realtimeSinceStartup - readySince >= 3f)
                break;
            yield return null;
        }
        TravelOptions.TravelOptionsMod travel = TravelOptions.TravelOptionsMod.Instance;
        bool ok = CanSave() && travel != null;
        Debug.LogFormat("VITARELAUNCH journey {0} after {1:F1} s: {2}", ok ? "resumed" : "not resumed (" + blocked + ")", Time.realtimeSinceStartup - start, resume);
        if (ok)
        {
            try { travel.VitaResumeJourney(resume); }
            catch (System.Exception e) { Debug.LogError("VITARELAUNCH journey: " + e.Message); }
        }
    }
#endif

    // After a relaunch's load: the camera pitch and weather the player had, set as the loading window closes
    IEnumerator RestoreView()
    {
        restoreView = false;
        while (SaveLoadManager.Instance.LoadInProgress || VitaLoadingWindow.Showing)
            yield return null;
        GameManager gm = GameManager.Instance;
        WeatherType saved = (WeatherType)restoreWeather, loaded = gm.WeatherManager.PlayerWeather.WeatherType;
        Debug.LogFormat("VITARELAUNCH view: pitch {0:F1} -> {1:F1}, weather after load {2}, before relaunch {3}", gm.PlayerMouseLook.Pitch, restorePitch,
            loaded, saved);
        gm.PlayerMouseLook.Pitch = restorePitch;
        if (!gm.PlayerEnterExit.IsPlayerInside && loaded != saved)
            gm.WeatherManager.SetWeather(saved);
    }

    static string blocked = "";
    static bool Blocked(string why)
    {
        blocked = why;
        return false;
    }

    // The loading window as it is on screen (picture, "Loading...", empty bar), for the next boot's first frames (VitaBootSplash):
    // width, height, then RGB565 rows bottom up. Best effort: without it the next boot shows the picture alone
    static void SaveScreen()
    {
        Texture2D shot = null;
        try
        {
            int w = Screen.width, h = Screen.height;
            shot = new Texture2D(w, h, TextureFormat.RGBA32, false);
            shot.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            byte[] px = shot.GetRawTextureData();
            // ReadPixels from the backbuffer comes back one channel shifted on this player (UNITY_VITA_GUIDE s8): bytes are A, R, G, B
            for (int i = 0, o = 0; i < px.Length; i += 4, o += 2)
            {
                int c = ((px[i + 1] & 0xF8) << 8) | ((px[i + 2] & 0xFC) << 3) | (px[i + 3] >> 3);
                px[o] = (byte)c;
                px[o + 1] = (byte)(c >> 8);
            }
            using (FileStream f = new FileStream(ScreenPath, FileMode.Create, FileAccess.Write))
            {
                f.Write(System.BitConverter.GetBytes(w), 0, 4);
                f.Write(System.BitConverter.GetBytes(h), 0, 4);
                f.Write(px, 0, w * h * 2);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("VITARELAUNCH screen: " + e.Message);
        }
        if (shot)
            Destroy(shot);
    }

    /// <summary>Boot scenes: the screen the relaunch left (SaveScreen), or null. The file is used once.</summary>
    public static Texture2D TakeScreen()
    {
        try
        {
            if (!File.Exists(ScreenPath))
                return null;
            byte[] d = File.ReadAllBytes(ScreenPath);
            File.Delete(ScreenPath);
            int w = System.BitConverter.ToInt32(d, 0), h = System.BitConverter.ToInt32(d, 4);
            if (w <= 0 || h <= 0 || d.Length != 8 + w * h * 2)
                return null;
            byte[] px = new byte[w * h * 2];
            System.Buffer.BlockCopy(d, 8, px, 0, px.Length);
            var tex = new Texture2D(w, h, TextureFormat.RGB565, false);
            tex.LoadRawTextureData(px);
            tex.Apply(false, true);
            return tex;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("VITARELAUNCH screen: " + e.Message);
            return null;
        }
    }

    IEnumerator Relaunch(string why)
    {
        busy = true;
        Debug.LogFormat("VITARELAUNCH {0}: {1}, GC heap {2:F1} MB, at {3} in {4}", why, VitaMemory.FreeMem(), System.GC.GetTotalMemory(false) / 1048576f,
            GameManager.Instance.PlayerObject.transform.position, GameManager.Instance.PlayerGPS.CurrentLocation.Name);
        VitaLoadingWindow window = new VitaLoadingWindow(DaggerfallUI.UIManager, null);
        DaggerfallUI.UIManager.PushWindow(window);
        yield return null;
        yield return null;      // the window drawn before the save's long frames
        // The loading window is copied for the next boot before this frame is shown, then the frame is cleared: what the player sees
        // is black with a notice (user, 2026-11-16: the dark restart looked like the Vita switching off)
        yield return new WaitForEndOfFrame();
        SaveScreen();
        GL.Clear(true, true, Color.black);
        window.ShowBlack();
        DaggerfallMessageBox notice = new DaggerfallMessageBox(DaggerfallUI.UIManager, window);
        notice.SetText(new string[] { TextManager.Instance.GetLocalizedText("vitaRelaunch1"), TextManager.Instance.GetLocalizedText("vitaRelaunch2") });
        notice.ClickAnywhereToClose = false;
        notice.AllowCancel = false;
        DaggerfallUI.UIManager.PushWindow(notice);
        float noticeFrom = Time.realtimeSinceStartup;
        yield return null;
        yield return null;      // the notice drawn before the save's long frames
        bool saved = false;
        yield return StartCoroutine(SaveLoadManager.Instance.VitaHiddenSave(SavePath, ok => saved = ok));
        while (saved && Time.realtimeSinceStartup - noticeFrom < NoticeSeconds)
            yield return null;
        if (saved)
        {
            try
            {
                if (journey != null)
                    File.WriteAllText(JourneyPath, journey);
                // Written last: a marker always has a complete save behind it
                File.WriteAllText(MarkerPath, string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}\n{1}\n{2}\n{3:R}", why,
                    VitaLoadingWindow.LastPicture ?? "", (int)GameManager.Instance.WeatherManager.PlayerWeather.WeatherType,
                    GameManager.Instance.PlayerMouseLook.Pitch));
                int result = VitaPower.Relaunch();      // returns only on failure
                Debug.LogFormat("VITARELAUNCH failed: {0:x}", result);
                File.Delete(MarkerPath);
                if (File.Exists(ScreenPath))
                    File.Delete(ScreenPath);
                if (File.Exists(JourneyPath))
                    File.Delete(JourneyPath);
            }
            catch (System.Exception e)
            {
                Debug.LogError("VITARELAUNCH: " + e.Message);
            }
        }
        // Didn't happen: carry on playing, don't try again this session (a stopped journey stays paused: the travel map offers to resume it)
        ForceNext = false;
        journeyStopped = false;
        Off = true;
        if (DaggerfallUI.UIManager.TopWindow == notice)
            notice.CloseWindow();
        if (DaggerfallUI.UIManager.TopWindow == window)
            window.CloseWindow();
        busy = false;
    }
}
#endif
