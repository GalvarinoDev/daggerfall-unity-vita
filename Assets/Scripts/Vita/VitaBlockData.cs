// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked BLOCKS.BSA records. BlocksFile.LoadBlock read each RMB/RDB/RDI record from disk and parsed it into
// a DFBlock (RDB fixes included) for every block of every city and dungeon; blocks.pak only holds their combined meshes. Assets/Editor/
// Vita/VitaBlockDataBaker.cs runs DFU's own BlocksFile.LoadBlock on the PC into StreamingAssets/VitaNative/blockdata.pak ("VBKD", VitaPack
// layout, LZ4 by vita/tools/lz4pack.py): per classic record name (key = VitaPack.NameKey) the finished DFBlock, written by VitaFields.
// Mod blocks (WorldDataReplacement) are read before LoadBlock as upstream. Miss: "VITANATIVE miss blockdata <name>", the record is parsed.
#if UNITY_PSP2
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using DaggerfallConnect;

public static class VitaBlockData
{
    public const int PackMagic = 0x444B4256;    // "VBKD"
    const int Magic = 0x524B4256;               // "VBKR", each record

    static VitaPack pack;

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "blockdata.pak"); }
    }

    /// <summary>The block as LoadBlock parses it; false if there is no pack (silent) or it isn't baked (logged).</summary>
    public static unsafe bool Read(string name, ref DFBlock dfBlock)
    {
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        if (pack.Count == 0)
            return false;
        VitaMemory.LoadCollect();   // dungeon layouts read every block in one frame
        uint key = VitaPack.NameKey(name);
        IntPtr memory = IntPtr.Zero, data;
        int length;
        try
        {
            object baked = null;
            if (pack.Read(key, out memory, out data, out length))
                baked = Parse(new UnmanagedMemoryStream((byte*)data, length), key);     // decoded in place: no managed record copy
            if (baked == null)
            {
                Debug.LogFormat("VITANATIVE miss blockdata {0}", name);
                return false;
            }
            dfBlock = (DFBlock)baked;
            return true;
        }
        finally
        {
            if (memory != IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>A record as Read decodes it (also the baker's check): a boxed DFBlock, or null if damaged.</summary>
    public static object Parse(byte[] bytes, uint key)
    {
        return Parse(new MemoryStream(bytes), key);
    }

    static object Parse(Stream stream, uint key)
    {
        try
        {
            using (var reader = new BinaryReader(stream))
                return reader.ReadInt32() == Magic && reader.ReadUInt32() == key ? VitaFields.Read(reader, typeof(DFBlock)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Bake (editor): one record.</summary>
    public static byte[] Save(string name, DFBlock dfBlock)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(VitaPack.NameKey(name));
            VitaFields.Write(writer, typeof(DFBlock), dfBlock);
            writer.Flush();
            return stream.ToArray();
        }
    }
}
#endif
