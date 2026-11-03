// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Weapon frame baker. Reads every WEAPO*.CIF with
// DFU's own CifRciFile, shelf-packs each file's frames (palette indices, rows bottom-up as GetColor32 lays them out, 2 texels apart) into
// the smallest power-of-two atlas (NPOT Alpha8 samples wrong on the Vita), and writes StreamingAssets/VitaNative/weapons.pak. Editor only.
//   vita/tools/bake_weapons.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output .pak, both Windows paths)

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;

public static class VitaWeaponBaker
{
    const int Gap = 2;

    public static void Bake()
    {
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        if (string.IsNullOrEmpty(output))
            output = VitaWeaponPack.PackPath;
        string[] files = Directory.GetFiles(arena2, "WEAPO*.CIF");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        if (files.Length == 0)
        {
            Debug.Log("VITABAKE failed: no WEAPO*.CIF in " + arena2);
            return;
        }

        using (var writer = new BinaryWriter(File.Create(output)))
        {
            writer.Write(VitaWeaponPack.Magic);
            writer.Write(VitaWeaponPack.Version);
            writer.Write(files.Length);
            foreach (string path in files)
            {
                var cif = new CifRciFile();
                cif.Load(path, FileUsage.UseMemory, true);
                var frames = new List<byte[]>();
                var sizes = new List<DFSize>();
                for (int record = 0; record < cif.RecordCount; record++)
                    for (int frame = 0; frame < cif.GetFrameCount(record); frame++)
                    {
                        DFSize size;
                        frames.Add(VitaPalette.Frame(cif.GetDFBitmap(record, frame), 0, 0, out size));
                        sizes.Add(size);
                    }

                int width = 0, height = 0;
                int[] place = null;
                for (int w = 64; w <= 2048; w *= 2)
                {
                    int h;
                    int[] p = Shelf(sizes, w, out h);
                    if (p != null && h <= 2048 && (place == null || w * h < width * height))
                    {
                        place = p;
                        width = w;
                        height = h;
                    }
                }
                if (place == null)
                {
                    Debug.Log("VITABAKE failed: " + Path.GetFileName(path) + " doesn't fit 2048x2048");
                    return;
                }

                var atlas = new byte[width * height];
                writer.Write(Path.GetFileName(path).ToUpperInvariant());
                writer.Write(width);
                writer.Write(height);
                writer.Write(frames.Count);
                for (int i = 0; i < frames.Count; i++)
                {
                    int x = place[i * 2], y = place[i * 2 + 1], fw = sizes[i].Width, fh = sizes[i].Height;
                    for (int row = 0; row < fh; row++)
                        Buffer.BlockCopy(frames[i], row * fw, atlas, (y + row) * width + x, fw);
                    writer.Write(x);
                    writer.Write(y);
                    writer.Write(fw);
                    writer.Write(fh);
                }
                writer.Write(atlas);
                Debug.LogFormat("VITABAKE weapon {0}: {1} frames, {2}x{3}", Path.GetFileName(path), frames.Count, width, height);
            }
        }
        Debug.Log("VITABAKE done: " + files.Length + " weapon files -> " + output);
    }

    // Tallest first, left to right in rows: x, y per frame and the power-of-two height used, or null if a frame is wider than width
    static int[] Shelf(List<DFSize> sizes, int width, out int height)
    {
        height = 0;
        var order = new List<int>();
        for (int i = 0; i < sizes.Count; i++)
            order.Add(i);
        order.Sort((a, b) => sizes[b].Height != sizes[a].Height ? sizes[b].Height.CompareTo(sizes[a].Height) : a.CompareTo(b));
        var place = new int[sizes.Count * 2];
        int x = 0, y = 0, rowHeight = 0;
        foreach (int i in order)
        {
            if (sizes[i].Width > width)
                return null;
            if (x + sizes[i].Width > width)
            {
                x = 0;
                y += rowHeight + Gap;
                rowHeight = 0;
            }
            place[i * 2] = x;
            place[i * 2 + 1] = y;
            x += sizes[i].Width + Gap;
            rowHeight = Math.Max(rowHeight, sizes[i].Height);
        }
        height = Mathf.NextPowerOfTwo(y + rowHeight);
        return place;
    }
}
