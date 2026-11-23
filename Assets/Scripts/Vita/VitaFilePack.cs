// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Small ARENA2 files from one pack. Finding a file in ARENA2 (1443 entries) takes 40-370 ms on
// the Vita, so every first read of a UI image or palette stalled the menu that wanted it: the travel map spent 1.76 s of its 2.01 s first
// open finding 11 images (ui1). vita/tools/filepack.py stores the files FileProxy keeps in its byte cache (IMG CIF RCI COL CFG BSA up to
// 256 KB, 363 files, 12 MB) unchanged in StreamingAssets/VitaNative/arena2.pak; FileProxy.LoadMemory reads them from here instead.
// Version 2 (vita/tools/lz4ownpacks.py): every file one LZ4 block, index entries name / offset / stored length / raw length.

#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallWorkshop;

public static class VitaFilePack
{
    const int Magic = 0x50465356;   // "VSFP"
    const int Version = 1;
    const int VersionLz4 = 2;

    static Dictionary<string, int[]> index;   // upper-case name -> offset, length (, stored length in version 2)
    static FileStream stream;
    static readonly object gate = new object();

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "arena2.pak"); }
    }

    /// <summary>The bytes of an ARENA2 file from the pack, or null (not in it, not in ARENA2, or no pack).</summary>
    public static byte[] Read(string filePath)
    {
        string folder = Path.GetDirectoryName(filePath);
        string arena2 = DaggerfallUnity.Instance.Arena2Path;
        if (folder == null || arena2 == null ||
            !string.Equals(folder.TrimEnd('/', '\\'), arena2.TrimEnd('/', '\\'), System.StringComparison.OrdinalIgnoreCase))
            return null;
        lock (gate)
        {
            if (index == null)
                Open();
            int[] entry;
            if (stream == null || !index.TryGetValue(Path.GetFileName(filePath).ToUpperInvariant(), out entry))
                return null;
            var data = new byte[entry[1]];
            byte[] block = entry.Length > 2 ? new byte[entry[2]] : data;
            stream.Seek(entry[0], SeekOrigin.Begin);
            int read = 0, n;
            while (read < block.Length && (n = stream.Read(block, read, block.Length - read)) > 0)
                read += n;
            if (read != block.Length)
                return null;
            return block == data || Decode(block, data) ? data : null;
        }
    }

    static unsafe bool Decode(byte[] block, byte[] data)
    {
        fixed (byte* src = block)
        fixed (byte* dst = data)
            return VitaLz4.Decode((System.IntPtr)src, block.Length, (System.IntPtr)dst, data.Length);
    }

    static void Open()
    {
        index = new Dictionary<string, int[]>();
        try
        {
            stream = File.OpenRead(PackPath);
            var reader = new BinaryReader(stream);
            int version = 0;
            if (reader.ReadInt32() != Magic || ((version = reader.ReadInt32()) != Version && version != VersionLz4))
            {
                stream.Dispose();
                stream = null;
                return;
            }
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(16)).TrimEnd('\0');
                if (version == VersionLz4)
                {
                    int offset = reader.ReadInt32(), stored = reader.ReadInt32();
                    index[name] = new[] { offset, reader.ReadInt32(), stored };
                }
                else
                    index[name] = new[] { reader.ReadInt32(), reader.ReadInt32() };
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("VitaFilePack: " + e.Message);
            if (stream != null)
                stream.Dispose();
            stream = null;
        }
    }
}
#endif
