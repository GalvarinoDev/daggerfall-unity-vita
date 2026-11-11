// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Chinese glyphs for the classic fonts. PC DFU draws CJK through TextMeshPro SDF fonts, which the Vita build leaves out. The Chinese
// VPK instead ships StreamingAssets/VitaNative/fonts_zh.pak (built on the PC): one 1-bit Alpha8 atlas holding exactly the
// characters its text uses, drawn from a pixel font, and one glyph table. DaggerfallFont puts it into its SDF glyph table, so text
// goes through DFU's existing per-codepoint SDF path (drawn with the pixel font material, no distance field). Per classic font the
// pack gives the point size (a whole number of screen pixels per font pixel at 960x544), the baseline and extra row height.
// Other VPKs have no pack: Load returns false and the classic byte fonts are used as before.
#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using DaggerfallWorkshop.Game.UserInterface;
using UnityEngine;

public static class VitaCjkFont
{
    const int Magic = 0x4B4A4356;    // "VCJK"

    static bool tried;
    static Texture2D atlas;          // shared by all five fonts, kept for the session (~0.5 MB)
    static Dictionary<int, DaggerfallFont.SDFGlyphInfo> glyphs;
    static float[] pointSize, baseline;
    static int[] rowExtra;

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "fonts_zh.pak"); }
    }

    /// <summary>The glyph table for one classic font (FontName index), or false when this VPK ships no pack.</summary>
    public static bool Load(int font, out DaggerfallFont.SDFFontInfo info, out int extraRows)
    {
        info = new DaggerfallFont.SDFFontInfo();
        extraRows = 0;
        if (!tried)
        {
            tried = true;
            Read();
        }
        if (atlas == null || font < 0 || font >= pointSize.Length)
            return false;
        info.pointSize = pointSize[font];
        info.baseline = baseline[font];
        info.atlasTexture = atlas;
        info.glyphs = glyphs;
        extraRows = rowExtra[font];
        return true;
    }

    static void Read()
    {
        byte[] b;
        try
        {
            b = File.ReadAllBytes(PackPath);    // opened directly: a miss is the normal case for non-Chinese VPKs
        }
        catch (IOException)
        {
            return;
        }
        using (var r = new BinaryReader(new MemoryStream(b)))
        {
            if (r.ReadInt32() != Magic || r.ReadInt32() != 1)
            {
                Debug.Log("VITANATIVE bad fonts_zh.pak");
                return;
            }
            int w = r.ReadInt32(), h = r.ReadInt32(), fonts = r.ReadInt32();
            pointSize = new float[fonts];
            baseline = new float[fonts];
            rowExtra = new int[fonts];
            for (int i = 0; i < fonts; i++)
            {
                pointSize[i] = r.ReadSingle();
                baseline[i] = r.ReadSingle();
                rowExtra[i] = r.ReadInt32();
            }
            int count = r.ReadInt32();
            glyphs = new Dictionary<int, DaggerfallFont.SDFGlyphInfo>(count);
            for (int i = 0; i < count; i++)
            {
                int code = r.ReadInt32();
                int x = r.ReadUInt16(), y = r.ReadUInt16(), gw = r.ReadUInt16(), gh = r.ReadUInt16();
                int bx = r.ReadInt16(), by = r.ReadInt16(), advance = r.ReadInt16();
                DaggerfallFont.SDFGlyphInfo g = new DaggerfallFont.SDFGlyphInfo();
                g.code = code;
                g.rect = new Rect((float)x / w, (float)y / h, (float)gw / w, (float)gh / h);
                g.offset = new Vector2(bx, by);
                g.size = new Vector2(gw, gh);
                g.advance = advance;
                glyphs[code] = g;
            }
            atlas = new Texture2D(w, h, TextureFormat.Alpha8, false);
            atlas.filterMode = FilterMode.Point;
            atlas.wrapMode = TextureWrapMode.Clamp;
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(b, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                atlas.LoadRawTextureData(new System.IntPtr(pin.AddrOfPinnedObject().ToInt64() + r.BaseStream.Position), w * h);
            }
            finally
            {
                pin.Free();
            }
            atlas.Apply(false, true);
        }
        Debug.LogFormat("VITANATIVE fonts_zh {0} glyphs", glyphs.Count);
    }
}
#endif
