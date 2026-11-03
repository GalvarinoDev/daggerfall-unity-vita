// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Boot music: the optional taiHEN plugin vita/bootmusic (*DFUV00001 in tai/config.txt) plays the title theme from process start,
// through the ~60 s engine load. It fades out when UserData/bootmusic.stop appears, which Stop() creates when the first video or
// the setup wizard starts. Upstream DFU is silent on the title menu after the intro, so the menu plays the title song here and the
// next video (new game cinematic) stops it again, on a player of its own: the game scene's DaggerfallUI has no song player (its
// SoundFont, so the extra player costs no extra bank. Without the plugin the flag file is harmless.
// RGB565, ~1 MB) on screen until Stop(). Unity keeps the last frame up during its blocking loads, but the startup scene does ~20 s
// of setup before its first frame, which was black after Unity's own splash. So the empty VitaBoot
// scene (build index 0) draws the art for a few frames first, then loads the startup scene.

#if UNITY_PSP2
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Utility;

public static class VitaBootMusic
{
    static bool stopped;
    static DaggerfallSongPlayer titlePlayer;

    public static void Stop()
    {
        if (stopped)
            return;
        stopped = true;
        VitaBootSplash.Hide();
        try
        {
            File.WriteAllText(Path.Combine(DaggerfallUnityApplication.PersistentDataPath, "bootmusic.stop"), "");
        }
        catch (IOException) { }
    }

    // Title menu (DaggerfallStartWindow.Update)
    public static void PlayTitleSong()
    {
        Stop();
        if (!titlePlayer)
            titlePlayer = new GameObject("VitaTitleSong").AddComponent<DaggerfallSongPlayer>();
        if (!titlePlayer.IsPlaying)
            titlePlayer.Play(SongFiles.song_5strong);
    }

    // Any video (DaggerfallVidPlayerWindow.OnPush: the intro stops the plugin, a new game cinematic the title song) and leaving the
    // title menu (DaggerfallStartWindow.OnPop: loading a save has no video)
    public static void StopTitleSong()
    {
        Stop();
        if (titlePlayer)
            Object.Destroy(titlePlayer.gameObject);
        titlePlayer = null;
    }
}
public class VitaBootSplash : MonoBehaviour
{
    static VitaBootSplash instance;
    Texture2D art;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init()
    {
        var go = new GameObject("VitaBootSplash");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<VitaBootSplash>();
        instance.art = Resources.Load<Texture2D>("VitaBootSplash");   // missing in a fresh clone: then only the scene hand-off
        if (SceneManager.GetActiveScene().buildIndex < SceneControl.StartupSceneIndex)
        {
            var cam = new GameObject("VitaBootCamera").AddComponent<Camera>();   // part of the VitaBoot scene, gone with it
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = 0;
        }
    }

    IEnumerator Start()
    {
        // Two presented frames with the art, then the startup scene (its setup blocks with the last frame on screen)
        yield return null;
        yield return null;
        if (SceneManager.GetActiveScene().buildIndex < SceneControl.StartupSceneIndex)
            SceneManager.LoadScene(SceneControl.StartupSceneIndex);
        if (!art)
            Hide();
    }

    void OnGUI()
    {
        if (!art)
            return;
        GUI.depth = -1000;      // over DaggerfallUI's own OnGUI
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), art, ScaleMode.ScaleAndCrop);
    }

    public static void Hide()
    {
        if (!instance)
            return;
        // Destroy waits for the end of the frame, so clear art first: OnGUI must not draw the unloaded texture (white) this frame
        var art = instance.art;
        instance.art = null;
        if (art)
            Resources.UnloadAsset(art);
        Destroy(instance.gameObject);
        instance = null;
    }
}
#endif
