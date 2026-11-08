// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Classic spell icons (runtime: SpellIconCollection.VitaTryLoadClassicBaked), cut as LoadClassicSpellIcons and LoadClassicSpellTargetAndElementIcons
// cut them, as raw RGBA32 rows bottom-up (LoadRawTextureData layout): StreamingAssets/VitaNative/spellicons-classic.bin.
// Format: 3 groups (spell, target, element), each width, height, count (int32 LE) then count * width * height * 4 bytes. Editor only.
//   vita/tools/bake_spellicons.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output file, both Windows paths)

using System;
using System.IO;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;

public static class VitaSpellIconBaker
{
    public static void Bake()
    {
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        using (BinaryWriter writer = new BinaryWriter(File.Create(output)))
        {
            const int rowCount = 20, spellCount = 69;
            int width, height;
            Color32[] atlas = Read(arena2, "ICON00I0.IMG", out width, out height);
            int dim = width / rowCount;
            Header(writer, dim, dim, spellCount);
            for (int i = 0; i < spellCount; i++)
                Icon(writer, atlas, width, height, new Rect(i % rowCount * dim, i / rowCount * dim, dim, dim));

            atlas = Read(arena2, "MASK04I0.IMG", out width, out height);
            Header(writer, 24, 16, 5);
            for (int i = 0; i < 5; i++)
                Icon(writer, atlas, width, height, new Rect(0, i * 16, 24, 16));
            Header(writer, 16, 16, 5);
            for (int i = 0; i < 5; i++)
                Icon(writer, atlas, width, height, new Rect(24, i * 16, 16, 16));
        }
        Debug.Log("VITABAKE done spell icons " + new FileInfo(output).Length + " bytes");
    }

    static Color32[] Read(string arena2, string name, out int width, out int height)
    {
        ImgFile img = new ImgFile(Path.Combine(arena2, name), FileUsage.UseMemory, true);
        img.LoadPalette(Path.Combine(arena2, img.PaletteName));
        DFBitmap bitmap = img.GetDFBitmap();
        width = bitmap.Width;
        height = bitmap.Height;
        return img.GetColor32(bitmap, 0);
    }

    static void Header(BinaryWriter writer, int width, int height, int count)
    {
        writer.Write(width);
        writer.Write(height);
        writer.Write(count);
    }

    // rect: origin top-left, as GetSubTexture's
    static void Icon(BinaryWriter writer, Color32[] atlas, int atlasWidth, int atlasHeight, Rect rect)
    {
        int x0 = (int)rect.x, y0 = atlasHeight - (int)rect.y - (int)rect.height;
        for (int y = 0; y < (int)rect.height; y++)
            for (int x = 0; x < (int)rect.width; x++)
            {
                Color32 c = atlas[(y0 + y) * atlasWidth + x0 + x];
                writer.Write(c.r);
                writer.Write(c.g);
                writer.Write(c.b);
                writer.Write(c.a);
            }
    }
}
