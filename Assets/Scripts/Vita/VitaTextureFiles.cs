// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked TEXTURE.### files for the CPU-side readers. textures.pak (VitaWorldTextures) holds what world materials
// upload; everything else that opens a TextureFile (item icons and paper-doll pieces through ImageReader, mobile NPCs, billboard sizes,
// atlas pieces on a miss) still parsed the 1996 file: record headers, RLE rows, solid colour archives. Assets/Editor/Vita/
// VitaTextureFileBaker.cs runs DFU's own TextureFile on the PC into StreamingAssets/VitaNative/texfiles.pak ("VTXF", VitaPack layout, LZ4
// by vita/tools/lz4pack.py): per archive a header record (HeaderKey: per image record offset, size, scale, frame count) and per image record
// one record with all its frames' index bytes (Key), so TextureFile.Load reads only the headers and each record is one read when first used.
// Miss: "VITANATIVE miss texfile <archive>", the file is parsed as upstream.
#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using DaggerfallConnect;

public static class VitaTextureFiles
{
    public const int PackMagic = 0x46585456;    // "VTXF"
    const int Magic = 0x52585456;               // "VTXR", each record
    public const int HeaderFields = 7;          // offset x, y, width, height, scale x, y, frame count

    public static System.Action<uint, byte[]> BakeSink;     // VitaTextureFileBaker

    static VitaPack pack;
    static readonly Dictionary<int, short[]> headers = new Dictionary<int, short[]>();     // null = not baked
    static readonly HashSet<uint> missed = new HashSet<uint>();

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "texfiles.pak"); }
    }

    public static uint Key(int archive, int record)
    {
        return (uint)(archive << 16 | record);
    }

    public static uint HeaderKey(int archive)
    {
        return (uint)(archive << 16 | 0xFFFF);
    }

    /// <summary>The archive's record headers (HeaderFields per record, shared: don't change), or null if it isn't baked.</summary>
    public static short[] Header(int archive)
    {
        short[] header;
        if (!headers.TryGetValue(archive, out header))
        {
            uint key = HeaderKey(archive);
            System.IntPtr memory, data;
            int length;
            header = Open(key, out memory, out data, out length) ? ParseHeader(data, length) : null;
            Free(memory);
            if (header == null)
                Miss(key);
            headers[archive] = header;
        }
        return header;
    }

    /// <summary>Fills frames (one image record) with its baked bitmaps; false if the record is missing or damaged.</summary>
    public static bool ReadFrames(int archive, int record, DFBitmap[] frames, DFPalette palette)
    {
        uint key = Key(archive, record);
        System.IntPtr memory, data;
        int length;
        DFBitmap[] baked = Open(key, out memory, out data, out length) ? ParseFrames(data, length, palette) : null;
        Free(memory);
        if (baked == null || baked.Length != frames.Length)
        {
            Miss(key);
            return false;
        }
        baked.CopyTo(frames, 0);
        return true;
    }

    static bool Open(uint key, out System.IntPtr memory, out System.IntPtr data, out int length)
    {
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        return pack.Read(key, out memory, out data, out length) && length >= 12 && Marshal.ReadInt32(data, 0) == Magic &&
            (uint)Marshal.ReadInt32(data, 4) == key;
    }

    static void Free(System.IntPtr memory)
    {
        if (memory != System.IntPtr.Zero)
            Marshal.FreeHGlobal(memory);
    }

    static void Miss(uint key)
    {
        if (pack.Count > 0 && missed.Add(key))
            Debug.LogFormat("VITANATIVE miss texfile {0} {1}", key >> 16, (key & 0xFFFF) == 0xFFFF ? "header" : (key & 0xFFFF).ToString());
    }

    /// <summary>A header record as Header decodes it (also VitaTextureFileBaker's check): null if damaged.</summary>
    public static short[] ParseHeader(System.IntPtr data, int length)
    {
        int count = Marshal.ReadInt32(data, 8);
        if (count < 0 || 12 + count * HeaderFields * 2 > length)
            return null;
        var header = new short[count * HeaderFields];
        Marshal.Copy(new System.IntPtr(data.ToInt64() + 12), header, 0, header.Length);
        return header;
    }

    /// <summary>A frames record as ReadFrames decodes it (also VitaTextureFileBaker's check): null if damaged.</summary>
    public static DFBitmap[] ParseFrames(System.IntPtr data, int length, DFPalette palette)
    {
        int count = Marshal.ReadInt32(data, 8), offset = 12;
        if (count < 0 || count > 0xFFFF)
            return null;
        var frames = new DFBitmap[count];
        for (int f = 0; f < count; f++)
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
            frames[f] = bitmap;
        }
        return frames;
    }

    /// <summary>Bake (editor): an archive's header record.</summary>
    public static void SaveHeader(int archive, short[] header)
    {
        Save(HeaderKey(archive), header.Length / HeaderFields, writer =>
        {
            foreach (short v in header)
                writer.Write(v);
        });
    }

    /// <summary>Bake (editor): one image record's frames.</summary>
    public static void SaveFrames(int archive, int record, DFBitmap[] frames)
    {
        Save(Key(archive, record), frames.Length, writer =>
        {
            foreach (DFBitmap bitmap in frames)
            {
                writer.Write(bitmap.Width);
                writer.Write(bitmap.Height);
                writer.Write(bitmap.Data, 0, bitmap.Width * bitmap.Height);
            }
        });
    }

    static void Save(uint key, int count, System.Action<BinaryWriter> body)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(key);
            writer.Write(count);
            body(writer);
            writer.Flush();
            BakeSink(key, stream.ToArray());
        }
    }
}
#endif
