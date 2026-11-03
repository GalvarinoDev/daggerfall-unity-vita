// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Classic sky baker (runtime side in Assets/Scripts/Vita/VitaClassicSky.cs): every day frame of SKY00-31.DAT (both hemispheres, the frame's
// palette) and NITE00-03.IMG (with the palette file it names), read with DFU's own SkyFile/ImgFile as DaggerfallSky's LoadVanillaDaySky/
// LoadVanillaNightSky do, into StreamingAssets/VitaNative/skies.pak. Editor only.
//   vita/tools/bake_skies.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output .pak, both Windows paths)

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;

public static class VitaSkyBaker
{
    public static void Bake()
    {
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        var records = new SortedDictionary<uint, byte[]>();
        VitaClassicSky.BakeSink = (key, record) => records[key] = record;
        int days = 0, nights = 0;
        for (int sky = 0; sky < 32; sky++)
        {
            string path = Path.Combine(arena2, SkyFile.IndexToFileName(sky));
            if (!File.Exists(path))
            {
                Debug.LogFormat("VITABAKE sky {0}: no file", sky);
                continue;
            }
            var skyFile = new SkyFile(path, FileUsage.UseMemory, true);
            for (int frame = 0; frame < 32; frame++)
            {
                DFBitmap east = skyFile.GetDFBitmap(0, frame), west = skyFile.GetDFBitmap(1, frame);
                VitaClassicSky.Save(VitaClassicSky.DayKey(sky, frame), Rgb(skyFile.GetDFPalette(frame)), east.Width, east.Height, east.Data, west.Data);
                days++;
            }
        }
        for (int night = 0; night < 4; night++)
        {
            string path = Path.Combine(arena2, string.Format("NITE{0:00}I0.IMG", night));
            if (!File.Exists(path))
            {
                Debug.LogFormat("VITABAKE night sky {0}: no file", night);
                continue;
            }
            var imgFile = new ImgFile(path, FileUsage.UseMemory, true);
            imgFile.Palette.Load(Path.Combine(arena2, imgFile.PaletteName));
            DFBitmap bitmap = imgFile.GetDFBitmap(0, 0);
            VitaClassicSky.Save(VitaClassicSky.NightFlag | (uint)night, Rgb(imgFile.Palette), bitmap.Width, bitmap.Height, bitmap.Data);
            nights++;
        }
        VitaClassicSky.BakeSink = null;

        // Header + index, then each record at a 512-byte offset
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        long total;
        using (var writer = new BinaryWriter(new FileStream(output, FileMode.Create)))
        {
            writer.Write(VitaClassicSky.PackMagic);
            writer.Write(VitaPack.Version);
            writer.Write(records.Count);
            long offset = AlignUp(12 + 12 * records.Count);
            foreach (KeyValuePair<uint, byte[]> record in records)
            {
                writer.Write(record.Key);
                writer.Write((int)offset);
                writer.Write(record.Value.Length);
                offset = AlignUp(offset + record.Value.Length);
            }
            offset = AlignUp(12 + 12 * records.Count);
            foreach (byte[] record in records.Values)
            {
                writer.Seek((int)offset, SeekOrigin.Begin);
                writer.Write(record);
                offset = AlignUp(offset + record.Length);
            }
            total = writer.BaseStream.Length;
        }
        Debug.LogFormat("VITABAKE done: {0} day frames, {1} night skies, {2:F1} MB -> {3}", days, nights, total / 1048576.0, output);
    }

    static byte[] Rgb(DFPalette palette)
    {
        var rgb = new byte[768];
        for (int i = 0; i < 256; i++)
        {
            rgb[i * 3] = palette.GetRed(i);
            rgb[i * 3 + 1] = palette.GetGreen(i);
            rgb[i * 3 + 2] = palette.GetBlue(i);
        }
        return rgb;
    }

    static long AlignUp(long position)
    {
        return (position + 511) & ~511L;
    }
}
