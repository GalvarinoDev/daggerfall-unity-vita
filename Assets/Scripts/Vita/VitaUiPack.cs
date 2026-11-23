// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked pixels for the Vita's own menus (quick menu ring, action ring, HUD style box): StreamingAssets/VitaNative/vitaui.pak, written
// in the editor by Assets/Editor/Vita/VitaUiBaker.cs with the menus' own drawing code (VitaQuickMenuWindow.BakeRecords). Textures
// are raw ARGB32 rows bottom-up, rings are the slice index per pixel followed by the base colours (RGBA). Without the pack the menus
// draw everything at runtime as before.

#if UNITY_PSP2
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public static class VitaUiPack
{
    public const int Magic = 0x50495556;   // "VUIP"
    public static bool Off;                // test harness A/B: draw at runtime
    static VitaPack pack;

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "vitaui.pak"); }
    }

    static VitaPack Pack
    {
        get
        {
            if (pack == null)
                pack = new VitaPack(PackPath, Magic);
            return pack;
        }
    }

    /// <summary>A new w x h ARGB32 texture from the baked record name (the caller owns it), or null.</summary>
    public static Texture2D Texture(string name, int w, int h, FilterMode filter)
    {
        if (Off)
            return null;
        System.IntPtr memory, data;
        int length;
        try
        {
            if (!Pack.Read(VitaPack.NameKey(name), out memory, out data, out length))
                return null;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("VitaUiPack: " + name + ": " + e.Message);
            return null;
        }
        try
        {
            if (length != w * h * 4)
                return null;
            Texture2D t = new Texture2D(w, h, TextureFormat.ARGB32, false);
            t.filterMode = filter;
            t.LoadRawTextureData(data, length);
            t.Apply(false, true);
            return t;
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>Fills slice (one byte per pixel) and colours from the baked ring for n slices; false if there is none.</summary>
    public static unsafe bool Ring(int n, byte[] slice, Color32[] colours)
    {
        if (Off)
            return false;
        System.IntPtr memory, data;
        int length;
        if (!Pack.Read(VitaPack.NameKey("ring" + n), out memory, out data, out length))
            return false;
        try
        {
            int count = slice.Length;
            if (length != count * 5 || colours.Length != count)
                return false;
            Marshal.Copy(data, slice, 0, count);
            fixed (Color32* dst = colours)
                System.Buffer.MemoryCopy((byte*)data + count, dst, count * 4, count * 4);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

#if UNITY_EDITOR
    /// <summary>Raw ARGB32 bytes of colours (rows bottom-up, as SetPixels32 takes them).</summary>
    public static byte[] Raw(Color32[] colours)
    {
        byte[] raw = new byte[colours.Length * 4];
        for (int i = 0; i < colours.Length; i++)
        {
            raw[i * 4] = colours[i].a;
            raw[i * 4 + 1] = colours[i].r;
            raw[i * 4 + 2] = colours[i].g;
            raw[i * 4 + 3] = colours[i].b;
        }
        return raw;
    }
#endif
}
#endif
