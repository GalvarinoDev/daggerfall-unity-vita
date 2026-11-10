// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Loading window for the Vita (game start, dungeon exit): a random picture from StreamingAssets/LoadScreens with "Loading..."
// below it, or the title text if there are no pictures. A DFU window, not OnGUI: a plain OnGUI drew under the HUD.

#if UNITY_PSP2
using System;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;

public class VitaLoadingWindow : DaggerfallBaseWindow
{
    readonly string category;
    Texture2D picture;
    Camera worldCamera;
    int worldCullingMask;
    bool audioWasPaused;

    public VitaLoadingWindow(IUserInterfaceManager uiManager, string category = null)
        : base(uiManager)
    {
        this.category = category;
    }

    // Loading bar: Progress is set by the load (never goes back); while the streaming world builds, its progress is
    // mapped into WorldFrom..WorldTo. The bar eases towards it on real time (the game is paused).
    public float WorldFrom = 0, WorldTo = 1;
    float progress, shown;
    Panel barBack, barFill;

    public float Progress
    {
        get { return progress; }
        set { progress = Mathf.Max(progress, Mathf.Clamp01(value)); }
    }

    protected override void Setup()
    {
        // The art already says Daggerfall: only "Loading..." and the bar in the black strip below it. The whole picture stays on screen
        // Fitted to the screen width, centred
#if !VITA_MODS
        picture = null;
#else
        picture = DaggerfallWorkshop.DaggerfallUnity.Settings.VitaLoadScreenArt ? LoadPicture(category) : null;
#endif
        if (picture != null)
        {
            ParentPanel.BackgroundTexture = picture;
            ParentPanel.BackgroundTextureLayout = BackgroundLayout.ScaleToFit;
            AddCentred(DaggerfallUI.DefaultFont, "Loading...", 183);
        }
        else
        {
            // Black parent panel (DaggerfallBaseWindow), text centred on the 320x200 native panel
            AddCentred(DaggerfallUI.TitleFont, "Daggerfall Unity", 75);
            AddCentred(DaggerfallUI.TitleFont, "Vita", 92);
            AddCentred(DaggerfallUI.DefaultFont, "Loading...", 183);
        }

        barBack = new Panel();
        barBack.BackgroundColor = Color.black;
        barBack.Outline.Enabled = true;
        barBack.Outline.Color = DaggerfallUI.DaggerfallDefaultTextColor;
        NativePanel.Components.Add(barBack);
        barFill = new Panel();
        barFill.BackgroundColor = DaggerfallUI.DaggerfallDefaultTextColor;
        NativePanel.Components.Add(barFill);
        barBack.Position = new Vector2(110, 192);
        barBack.Size = new Vector2(100, 4);
        barFill.Position = new Vector2(110, 192);
        barFill.Size = new Vector2(0, 4);
    }

    // The game is paused behind this window and the world under it is half built: the main camera draws nothing meanwhile, so the
    // frames the staggered builds yield go to loading instead of culling and submitting the world (city: ~1400 draws, 80 ms a frame)
    public static bool Showing;     // VitaMemory.LoadCollect: collections only behind this screen

    public override void OnPush()
    {
        base.OnPush();
        Showing = true;
        worldCamera = GameManager.HasInstance ? GameManager.Instance.MainCamera : null;
        if (worldCamera != null)
        {
            worldCullingMask = worldCamera.cullingMask;
            worldCamera.cullingMask = 0;
        }
        // Silence while loading: sounds started during the build otherwise stutter or repeat through the long frames
        audioWasPaused = AudioListener.pause;
        AudioListener.pause = true;
        KeepScreenOn();
    }

    // The Vita dims and then sleeps counting from the last button press, and a Palace load is ~90 s with no input. Normal play keeps
    // the system timer; loads reset it. A dungeon builds in one long frame, so this runs before and after it
    public override void Update()
    {
        base.Update();
        if (Time.realtimeSinceStartup - lastTick > 5)
            KeepScreenOn();
        if (GameManager.HasInstance && GameManager.Instance.StreamingWorld.VitaIsBuilding)
            Progress = WorldFrom + (WorldTo - WorldFrom) * GameManager.Instance.StreamingWorld.VitaBuildProgress;
        // Half the gap per drawn frame: smooth at 30 FPS, and still on time through a load's long frames (a per-second cap left the bar
        // at 5% with the load at 73%, loadbar1)
        shown = Mathf.Lerp(shown, progress, 0.5f);
        if (barFill != null)
            barFill.Size = new Vector2(100 * shown, 4);
        if ((int)(progress * 10) != loggedTenth)
        {
            loggedTenth = (int)(progress * 10);
            Debug.LogFormat("VITALOADBAR {0:F2}", progress);
        }
        // Stuck loading window: every 10 s, what the loads are still waiting for
        if (Time.realtimeSinceStartup - lastState > 10)
        {
            if (lastState == 0)
                pushed = Time.realtimeSinceStartup;
            lastState = Time.realtimeSinceStartup;
            if (GameManager.HasInstance)
                Debug.LogFormat("VITALOADWAIT {0:F0} s: load {1} respawning {2} world {3} windows {4} top {5} timeScale {6}",
                    lastState - pushed, GameManager.Instance.SaveLoadManager.LoadInProgress, GameManager.Instance.PlayerEnterExit.IsRespawning,
                    GameManager.Instance.StreamingWorld.VitaBuildState, uiManager.WindowCount, uiManager.TopWindow == this ? "this" : uiManager.TopWindow.GetType().Name,
                    Time.timeScale);
        }
    }

    float lastTick, lastState, pushed;
    int loggedTenth = -1;

    void KeepScreenOn()
    {
        lastTick = Time.realtimeSinceStartup;
#if !UNITY_EDITOR
        UnityEngine.PSVita.Utility.PowerTick(UnityEngine.PSVita.Utility.PowerTickType.Default);
#endif
    }

    public override void OnPop()
    {
        base.OnPop();
        Showing = false;
        VitaMemory.IdleCollect("load end");   // last frame behind the picture: play starts with the count at 0
        Debug.LogFormat("VITALOADGC {0} load collections so far", VitaMemory.LoadCollections);
        KeepScreenOn();
        AudioListener.pause = audioWasPaused;
        if (worldCamera != null)
            worldCamera.cullingMask = worldCullingMask;
        worldCamera = null;
        if (picture != null)
            UnityEngine.Object.Destroy(picture);
        picture = null;
    }

    void AddCentred(DaggerfallFont font, string text, float y)
    {
        TextLabel label = DaggerfallUI.AddTextLabel(font, new Vector2(0, y), text, NativePanel);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.TextColor = DaggerfallUI.DaggerfallDefaultTextColor;
    }

    // StreamingAssets/LoadScreens holds raw RGB565 files from vita/tools/loadscreens.py, listed in list.txt ("w h file"),
    // so nothing is decoded or listed on the Vita. Files whose name starts with category are preferred.
    static Texture2D LoadPicture(string category)
    {
        try
        {
            string dir = System.IO.Path.Combine(Application.streamingAssetsPath, "LoadScreens");
            string[] lines = System.IO.File.ReadAllLines(System.IO.Path.Combine(dir, "list.txt"));
            List<string> picks = new List<string>();
            foreach (string line in lines)
                if (category != null && line.Contains(" " + category + "_"))
                    picks.Add(line);
            if (picks.Count == 0)
                picks.AddRange(lines);
            string[] fields = picks[UnityEngine.Random.Range(0, picks.Count)].Split(' ');
            Texture2D texture = new Texture2D(int.Parse(fields[0]), int.Parse(fields[1]), TextureFormat.RGB565, false);
            texture.LoadRawTextureData(System.IO.File.ReadAllBytes(System.IO.Path.Combine(dir, fields[2])));
            texture.Apply(false, true);
            return texture;
        }
        catch (Exception e)
        {
            Debug.LogWarning("VitaLoadingWindow: no picture: " + e.Message);
            return null;
        }
    }
}
#endif
