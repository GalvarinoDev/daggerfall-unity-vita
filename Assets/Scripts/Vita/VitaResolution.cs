// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Outside, the frame is fragment bound; dungeons and interiors are
// CPU bound. So outside the whole screen drops to 720x408 and the display scaler upscales
// it (free bilinear); inside it goes back to the configured resolution. Only when 960x544 is configured (a lower setting is kept).

#if UNITY_PSP2
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;

public class VitaResolution : MonoBehaviour
{
    const int lowWidth = 720, lowHeight = 408;
    const int checkFrames = 15;

    int lastState = -1;     // -1 = not in game, 0 = inside, 1 = outside

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        GameObject go = new GameObject("VitaResolution");
        DontDestroyOnLoad(go);
        go.AddComponent<VitaResolution>();
    }

    void Update()
    {
        if (Time.frameCount % checkFrames != 0)
            return;
        PlayerEnterExit playerEnterExit = GameManager.HasInstance ? GameManager.Instance.PlayerEnterExit : null;
        int state = !playerEnterExit ? -1 : playerEnterExit.IsPlayerInside ? 0 : 1;
        if (state == lastState)
            return;
        lastState = state;

        SettingsManager settings = DaggerfallUnity.Settings;
        bool low = state == 1 && settings.VitaExteriorLowRes && settings.ResolutionWidth == 960;
        int width = low ? lowWidth : settings.ResolutionWidth;
        int height = low ? lowHeight : settings.ResolutionHeight;
        if (Screen.width != width || Screen.height != height)
        {
            Screen.SetResolution(width, height, true);
            Debug.LogFormat("VITARES {0} -> {1}x{2}", state == 1 ? "outside" : state == 0 ? "inside" : "menu", width, height);
        }
    }
}
#endif
