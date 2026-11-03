// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Classic font atlas baker (runtime side in Assets/Scripts/Vita/VitaFonts.cs): FONT0000-0004.FNT from StreamingAssets/Fonts through DFU's
// own ImageProcessing.CreateFontAtlas, as DaggerfallFont.LoadFont builds them, into StreamingAssets/VitaNative/fonts.pak. Each record is
// loaded back the Vita's way (VitaFonts.ParseRects, LoadRawTextureData) and compared pixel by pixel before it is written. Editor only.
//   vita/tools/bake_fonts.sh   (VITA_BAKE_OUT = output .pak, a Windows path)

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Utility;

public static class VitaFontBaker
{
    public static void Bake()
    {
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        var records = new SortedDictionary<uint, byte[]>();
        byte[] last = null;
        VitaFonts.BakeSink = (key, bytes) => records[key] = last = bytes;
        int fonts = 0, bad = 0;
        foreach (DaggerfallFont.FontName font in Enum.GetValues(typeof(DaggerfallFont.FontName)))
        {
            var fnt = new FntFile();
            if (!fnt.Load(Path.Combine(Path.Combine(Application.dataPath, "StreamingAssets/Fonts"), font + ".FNT"), FileUsage.UseMemory, true))
            {
                Debug.LogFormat("VITABAKE font {0}: not readable", font);
                bad++;
                continue;
            }
            Texture2D atlas;
            Rect[] rects;
            ImageProcessing.CreateFontAtlas(fnt, Color.clear, Color.white, out atlas, out rects);
            if (atlas.format != TextureFormat.ARGB32 || atlas.width != VitaFonts.AtlasDim || atlas.mipmapCount != 1 || rects.Length != VitaFonts.Glyphs)
            {
                Debug.LogFormat("VITABAKE font {0}: atlas is not what VitaFonts loads", font);
                bad++;
                continue;
            }
            VitaFonts.Save((int)font, rects, atlas.GetRawTextureData());
            if (!Same(atlas, rects, last))
            {
                Debug.LogFormat("VITABAKE font {0}: loaded record differs", font);
                bad++;
            }
            UnityEngine.Object.DestroyImmediate(atlas);
            fonts++;
        }
        VitaFonts.BakeSink = null;
        long total = VitaPackWriter.Write(output, VitaFonts.PackMagic, records);
        Debug.LogFormat("VITABAKE done: {0} fonts, {1} differ, {2:F1} MB -> {3}", fonts, bad, total / 1048576.0, output);
    }

    static bool Same(Texture2D atlas, Rect[] rects, byte[] record)
    {
        GCHandle pin = GCHandle.Alloc(record, GCHandleType.Pinned);
        var loaded = new Texture2D(VitaFonts.AtlasDim, VitaFonts.AtlasDim, TextureFormat.ARGB32, false);
        try
        {
            Rect[] decoded = VitaFonts.ParseRects(pin.AddrOfPinnedObject());
            for (int i = 0; i < rects.Length; i++)
                if (decoded[i] != rects[i])
                    return false;
            loaded.LoadRawTextureData(new IntPtr(pin.AddrOfPinnedObject().ToInt64() + VitaFonts.AtlasOffset), VitaFonts.AtlasBytes);
            Color32[] a = atlas.GetPixels32(), b = loaded.GetPixels32();
            for (int i = 0; i < a.Length; i++)
                if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a)
                    return false;
            return true;
        }
        finally
        {
            pin.Free();
            UnityEngine.Object.DestroyImmediate(loaded);
        }
    }
}
