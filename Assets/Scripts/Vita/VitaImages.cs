// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked classic UI images. Every IMG, CIF, RCI, CFA, BSS and GFX file in ARENA2 was parsed on the Vita by its
// reader class (headers, RLE rows, headerless-size tables, palettized IMGs x4) whenever a window, the HUD or the paper doll opened one.
// Assets/Editor/Vita/VitaImageBaker.cs runs DFU's own readers on the PC and stores what they produce in StreamingAssets/VitaNative/images.pak
// ("VIMG", VitaPack layout, LZ4 by vita/tools/lz4pack.py), one record per file keyed by NameKey: per image record its size, offset and frame
// count, the palette for palettized IMGs, then every frame's index bytes. Each reader's Load takes a File from Read instead of parsing
// (miss: "VITANATIVE miss image <name>", the file is parsed as upstream).
#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;

public static class VitaImages
{
    public const int PackMagic = 0x474D4956;    // "VIMG"
    const int Magic = 0x464D4956;               // "VIMF", each record

    public static System.Action<uint, byte[]> BakeSink;     // VitaImageBaker

    public sealed class Record
    {
        public int Width, Height, X, Y;
        public DFBitmap[] Frames;
    }

    public sealed class File
    {
        public Record[] Records;
        public byte[] Palette;      // 768-byte RGB as the reader left it (palettized IMGs), else null
    }

    static VitaPack pack;
    static readonly HashSet<string> missed = new HashSet<string>();

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "images.pak"); }
    }

    /// <summary>The baked image file for an ARENA2 path (frames use palette), or null: not ARENA2, no pack or not baked (logged once).</summary>
    public static File Read(string filePath, DFPalette palette)
    {
        string folder = Path.GetDirectoryName(filePath), arena2 = DaggerfallUnity.Instance.Arena2Path;
        if (folder == null || arena2 == null ||
            !string.Equals(folder.TrimEnd('/', '\\'), arena2.TrimEnd('/', '\\'), System.StringComparison.OrdinalIgnoreCase))
            return null;
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        if (pack.Count == 0)
            return null;
        string name = Path.GetFileName(filePath);
        uint key = VitaPack.NameKey(name);
        System.IntPtr memory = System.IntPtr.Zero, data;
        int length;
        try
        {
            File file = !pack.Read(key, out memory, out data, out length) || length < 16 || Marshal.ReadInt32(data, 0) != Magic ||
                (uint)Marshal.ReadInt32(data, 4) != key ? null : Parse(data, length, palette);
            if (file == null && missed.Add(name.ToUpperInvariant()))
                Debug.LogFormat("VITANATIVE miss image {0}", name);
            return file;
        }
        finally
        {
            if (memory != System.IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>A record as Read decodes it (also VitaImageBaker's check): null if it is damaged.</summary>
    public static File Parse(System.IntPtr data, int length, DFPalette palette)
    {
        var file = new File();
        int count = Marshal.ReadInt32(data, 8), offset = 16;
        if (Marshal.ReadInt32(data, 12) != 0)
        {
            if (offset + 768 > length)
                return null;
            file.Palette = new byte[768];
            Marshal.Copy(new System.IntPtr(data.ToInt64() + offset), file.Palette, 0, 768);
            offset += 768;
        }
        file.Records = new Record[count];
        for (int r = 0; r < count; r++)
        {
            if (offset + 20 > length)
                return null;
            var record = new Record();
            record.Width = Marshal.ReadInt32(data, offset);
            record.Height = Marshal.ReadInt32(data, offset + 4);
            record.X = Marshal.ReadInt32(data, offset + 8);
            record.Y = Marshal.ReadInt32(data, offset + 12);
            record.Frames = new DFBitmap[Marshal.ReadInt32(data, offset + 16)];
            offset += 20;
            for (int f = 0; f < record.Frames.Length; f++)
            {
                if (offset + 8 > length)
                    return null;
                int width = Marshal.ReadInt32(data, offset), height = Marshal.ReadInt32(data, offset + 4);
                if (width < 0 || height < 0 || offset + 8 + width * height > length)
                    return null;
                var bitmap = new DFBitmap(width, height);
                bitmap.Palette = palette;
                Marshal.Copy(new System.IntPtr(data.ToInt64() + offset + 8), bitmap.Data, 0, bitmap.Data.Length);
                offset += 8 + bitmap.Data.Length;
                record.Frames[f] = bitmap;
            }
            file.Records[r] = record;
        }
        return file;
    }

    /// <summary>Bake (editor): one file's record from what its reader produced.</summary>
    public static void Save(string fileName, File file)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(VitaPack.NameKey(fileName));
            writer.Write(file.Records.Length);
            writer.Write(file.Palette != null ? 1 : 0);
            if (file.Palette != null)
                writer.Write(file.Palette);
            foreach (Record record in file.Records)
            {
                foreach (int v in new[] { record.Width, record.Height, record.X, record.Y, record.Frames.Length })
                    writer.Write(v);
                foreach (DFBitmap bitmap in record.Frames)
                {
                    writer.Write(bitmap.Width);
                    writer.Write(bitmap.Height);
                    writer.Write(bitmap.Data, 0, bitmap.Width * bitmap.Height);
                }
            }
            writer.Flush();
            BakeSink(VitaPack.NameKey(fileName), stream.ToArray());
        }
    }
}
#endif
