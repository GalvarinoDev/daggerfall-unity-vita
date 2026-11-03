// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Unity sends every behaviour with an OnGUI a Layout event before each Repaint/input event, only needed for GUILayout/GUI.Window.
// No runtime DFU code uses either (mods are off on the Vita), so after each scene load every OnGUI behaviour gets
// useGUILayout = false: same drawing, one OnGUI call less per behaviour per frame.

#if UNITY_PSP2
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class VitaGuiLayout
{
    public static bool Skip = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init()
    {
        SceneManager.sceneLoaded += (scene, mode) => Apply(!Skip);
    }

    /// <summary>Sets useGUILayout on every behaviour with an OnGUI; returns how many.</summary>
    public static int Apply(bool layout)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        int count = 0;
        foreach (MonoBehaviour behaviour in Object.FindObjectsOfType<MonoBehaviour>())
            if (behaviour.GetType().GetMethod("OnGUI", flags) != null)
            {
                behaviour.useGUILayout = layout;
                count++;
            }
        return count;
    }
}
#endif
