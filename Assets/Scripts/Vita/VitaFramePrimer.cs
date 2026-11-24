// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// One transparent 1x1 texture draw on the player camera before it renders, every frame (the camera's own clear wipes it).
// Unity's Vita player calls sceGxmMidSceneFlush twice a frame and waits for the GPU's vertex work there, apparently the first
// time the frame needs its dynamic geometry buffer (immediate draws: HUD, DrawTexture). Left alone, that came after most of the
// scene's draws and stalled the render thread ~5.6 ms (start dungeon, GXM call timeline); with this draw first, the flush runs
// at scene start with nothing queued. Start dungeon 91 -> 97 FPS, Daggerfall at noon 31 -> 37 (Dynamic Skies on). The vanilla sky
// rig's own DrawTexture in an earlier camera did the same by accident.

#if UNITY_PSP2
using UnityEngine;
using DaggerfallWorkshop.Game;

public static class VitaFramePrimer
{
    public static bool On = true;   // test switch
    static readonly Rect pixel = new Rect(0, 0, 1, 1);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init()
    {
        Camera.onPreRender += Prime;    // static method, lives as long as the game
    }

    static void Prime(Camera cam)
    {
        if (!On || !GameManager.HasInstance || cam != GameManager.Instance.MainCamera)
            return;
        GL.PushMatrix();
        GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
        Graphics.DrawTexture(pixel, Texture2D.whiteTexture, pixel, 0, 0, 0, 0, Color.clear);
        GL.PopMatrix();
    }
}
#endif
