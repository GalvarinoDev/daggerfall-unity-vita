// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// One baked pack in StreamingAssets/VitaNative: header magic, version, count, then per record key / offset /
// length (version 1) or key / offset / stored length / raw length (version 2, LZ4 blocks from vita/tools/lz4pack.py), records at 512-byte
// offsets. The index is read once; each record is one sceIoPread into 64-byte-aligned native memory, decoded in
// native memory, so a read makes no managed garbage. Used by VitaAtlasCache (atlas.pak) and VitaModelPack (models.pak).

#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public sealed class VitaPack
{
    public const int Version = 1;      // what the editor bakers write
    public const int VersionLz4 = 2;   // after vita/tools/lz4pack.py

#if !UNITY_EDITOR
    [DllImport("__Internal")] static extern int sceIoOpen(string path, int flags, int mode);
    [DllImport("__Internal")] static extern int sceIoPread(int fd, System.IntPtr buffer, int size, long offset);
#endif

    readonly string path;
    // A struct, not an int[] per record (one small array object each, 10-15k per pack for the GC to mark); with no references in the
    // entries the dictionary's arrays are pointer-free, so the GC doesn't scan them either
    struct Entry
    {
        public int offset, stored, raw;
    }
    readonly Dictionary<uint, Entry> index = new Dictionary<uint, Entry>();   // key -> offset, stored length, raw length
    readonly int fd = -1;                                                      // app0: (not invalidated by suspend), one for the session
    readonly bool lz4;

    /// <summary>Reads the index of the pack at path; an empty pack (Count 0) if it's missing or not a pack with this magic.</summary>
    public VitaPack(string path, int magic)
    {
        this.path = path;
#if !UNITY_EDITOR
        try
        {
            using (var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096)))
            {
                int version = reader.ReadInt32() == magic ? reader.ReadInt32() : 0;
                if (version != Version && version != VersionLz4)
                    throw new IOException("not a version " + Version + " or " + VersionLz4 + " pack");
                lz4 = version == VersionLz4;
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    uint key = reader.ReadUInt32();
                    int offset = reader.ReadInt32(), length = reader.ReadInt32();
                    index[key] = new Entry { offset = offset, stored = length, raw = lz4 ? reader.ReadInt32() : length };
                }
            }
            // streamingAssetsPath is "/app0/Media/StreamingAssets": FileStream takes it, sceIoOpen needs the device form (atp1: 0x80010016)
            string device = path.StartsWith("/app0/") ? "app0:" + path.Substring(6) : path;
            fd = sceIoOpen(device, 1, 0);   // SCE_O_RDONLY
            if (fd < 0)
                throw new IOException(string.Format("sceIoOpen {0:x8}", fd));
            Debug.LogFormat("VitaPack: {0}: {1} records{2}", Path.GetFileName(path), index.Count, lz4 ? " (LZ4)" : "");
        }
        catch (System.Exception e)
        {
            Debug.LogWarningFormat("VitaPack: {0} not loaded: {1}", path, e.Message);
            index.Clear();
        }
#endif
    }

    /// <summary>A record key from a file or collection name: FNV-1a over the upper-case name.</summary>
    public static uint NameKey(string name)
    {
        uint hash = 2166136261;
        foreach (char c in name.ToUpperInvariant())
            hash = (hash ^ c) * 16777619;
        return hash;
    }

    public int Count
    {
        get { return index.Count; }
    }

    public bool Contains(uint key)
    {
        return index.ContainsKey(key);
    }

    /// <summary>The record for key, decoded, at data (64-byte aligned); memory is what to free (Marshal.FreeHGlobal), also on failure.</summary>
    public bool Read(uint key, out System.IntPtr memory, out System.IntPtr data, out int length)
    {
        Entry entry;
        if (!index.TryGetValue(key, out entry))
        {
            memory = data = System.IntPtr.Zero;
            length = 0;
            return false;
        }
        length = entry.raw;
        if (!lz4)
            return ReadAligned(fd, entry.offset, length, path, out memory, out data);
        System.IntPtr stored, storedData;
        bool read = ReadAligned(fd, entry.offset, entry.stored, path, out stored, out storedData);
        try
        {
            memory = Marshal.AllocHGlobal(length + 64);
            data = new System.IntPtr((memory.ToInt64() + 63) & ~63L);
            if (read && VitaLz4.Decode(storedData, entry.stored, data, length))
                return true;
            Debug.LogWarningFormat("VitaPack: {0}: damaged LZ4 record {1:x8}", path, key);
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(stored);
        }
    }

    /// <summary>length bytes at offset of fd into 64-byte-aligned native memory (one pread); memory is what to free, also on failure.</summary>
    public static bool ReadAligned(int fd, long offset, int length, string path, out System.IntPtr memory, out System.IntPtr data)
    {
        memory = Marshal.AllocHGlobal(length + 64);
        data = new System.IntPtr((memory.ToInt64() + 63) & ~63L);
#if UNITY_EDITOR
        return false;
#else
        int read = sceIoPread(fd, data, length, offset);
        if (read != length)
        {
            Debug.LogWarningFormat("VitaPack: {0}: read {1:x8} of {2} bytes at {3}", path, read, length, offset);
            return false;
        }
        return true;
#endif
    }
}
#endif
