// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked classic font atlases. DaggerfallFont.LoadFont turned each FONT0000-0004.FNT into a 256x256 ARGB32
// atlas on the Vita (240 glyphs expanded bit by bit into Color32 arrays, copied in, SetPixels32 + upload). Assets/Editor/Vita/VitaFontBaker.cs
// runs DFU's own ImageProcessing.CreateFontAtlas on the PC into StreamingAssets/VitaNative/fonts.pak ("VFNT", VitaPack layout, LZ4 by
// vita/tools/lz4pack.py): per font (key = FontName) the 240 glyph rects and the atlas's raw texture bytes, loaded with LoadRawTextureData.
// Miss: "VITANATIVE miss font <n>", the atlas is built as upstream.
#if UNITY_PSP2
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public static class VitaFonts
{
    public const int PackMagic = 0x544E4656;    // "VFNT"
    const int Magic = 0x524E4656;               // "VFNR", each record
    public const int AtlasDim = 256, Glyphs = 240;
    public const int RectsOffset = 16, AtlasOffset = 4096, AtlasBytes = AtlasDim * AtlasDim * 4;

    public static System.Action<uint, byte[]> BakeSink;     // VitaFontBaker

    static VitaPack pack;

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "fonts.pak"); }
    }

    /// <summary>The baked atlas (ARGB32, no mips, as CreateFontAtlas makes it) and its glyph rects, or false (logged).</summary>
    public static bool Load(int font, out Texture2D atlas, out Rect[] rects)
    {
        atlas = null;
        rects = null;
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        System.IntPtr memory = System.IntPtr.Zero, data;
        int length;
        try
        {
            if (!pack.Read((uint)font, out memory, out data, out length) || length < AtlasOffset + AtlasBytes ||
                Marshal.ReadInt32(data, 0) != Magic || Marshal.ReadInt32(data, 4) != font)
            {
                if (pack.Count > 0)
                    Debug.LogFormat("VITANATIVE miss font {0}", font);
                return false;
            }
            rects = ParseRects(data);
            atlas = new Texture2D(AtlasDim, AtlasDim, TextureFormat.ARGB32, false);
            atlas.LoadRawTextureData(new System.IntPtr(data.ToInt64() + AtlasOffset), AtlasBytes);
            atlas.Apply(false);
            return true;
        }
        finally
        {
            if (memory != System.IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>The glyph rects of a record (also VitaFontBaker's check).</summary>
    public static Rect[] ParseRects(System.IntPtr data)
    {
        var values = new float[Glyphs * 4];
        Marshal.Copy(new System.IntPtr(data.ToInt64() + RectsOffset), values, 0, values.Length);
        var rects = new Rect[Glyphs];
        for (int i = 0; i < Glyphs; i++)
            rects[i] = new Rect(values[i * 4], values[i * 4 + 1], values[i * 4 + 2], values[i * 4 + 3]);
        return rects;
    }

    /// <summary>Bake (editor): one font's record.</summary>
    public static void Save(int font, Rect[] rects, byte[] atlasBytes)
    {
        var record = new byte[AtlasOffset + AtlasBytes];
        System.BitConverter.GetBytes(Magic).CopyTo(record, 0);
        System.BitConverter.GetBytes(font).CopyTo(record, 4);
        System.BitConverter.GetBytes(AtlasDim).CopyTo(record, 8);
        for (int i = 0; i < Glyphs; i++)
        {
            System.BitConverter.GetBytes(rects[i].x).CopyTo(record, RectsOffset + i * 16);
            System.BitConverter.GetBytes(rects[i].y).CopyTo(record, RectsOffset + i * 16 + 4);
            System.BitConverter.GetBytes(rects[i].width).CopyTo(record, RectsOffset + i * 16 + 8);
            System.BitConverter.GetBytes(rects[i].height).CopyTo(record, RectsOffset + i * 16 + 12);
        }
        System.Array.Copy(atlasBytes, 0, record, AtlasOffset, AtlasBytes);
        BakeSink((uint)font, record);
    }
}
#endif
