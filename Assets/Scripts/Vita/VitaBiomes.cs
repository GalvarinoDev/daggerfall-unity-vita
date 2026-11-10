// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// World of Daggerfall - Biomes' billboard swap (WODClimates.cs): nature archive 501 becomes DET's
// Mediterranean set 10030 on the orange pixels of its climate map, baked by vita/tools/biomespack.py into biomes_nature.bin
// (1000 x 500 bits, 125 bytes per row). The PC swaps after the layout, so the tree records with a low poly model stay archive 501's.

#if UNITY_PSP2
using System.IO;
using DaggerfallWorkshop;
using UnityEngine;

public static class VitaBiomes
{
    public const int MediterraneanArchive = 10030;
    const int SwappedArchive = 501;
    const int Width = 1000, Height = 500, RowBytes = Width / 8;

    static byte[] mask;

    public static int NatureArchive(int archive, int mapPixelX, int mapPixelY)
    {
        if (archive != SwappedArchive || !DaggerfallUnity.Settings.VitaWorldOfDaggerfallBiomes)
            return archive;
        if (mask == null)
        {
            try
            {
                mask = File.ReadAllBytes(Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "biomes_nature.bin"));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("VitaBiomes: " + e.Message);
                mask = new byte[0];
            }
        }
        if (mask.Length != RowBytes * Height || mapPixelX < 0 || mapPixelX >= Width || mapPixelY < 0 || mapPixelY >= Height)
            return archive;
        return (mask[mapPixelY * RowBytes + (mapPixelX >> 3)] >> (mapPixelX & 7) & 1) != 0 ? MediterraneanArchive : archive;
    }

    /// <summary>The archive whose low poly tree models apply to a nature batch of this archive.</summary>
    public static int ModelArchive(int archive)
    {
        return archive == MediterraneanArchive ? SwappedArchive : archive;
    }
}
#endif
