// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked MAPS.BSA regions and locations, CLIMATE.PAK and POLITIC.PAK. MapsFile read each region's map table
// and names from MAPS.BSA, and on the first location read the region's MapPItem/MapDItem records whole (up to 2 MB) to parse one location;
// PakFile unrolled both PAK files' runs at boot. Assets/Editor/Vita/VitaMapBaker.cs runs DFU's own MapsFile/PakFile on the PC into
// StreamingAssets/VitaNative/maps.pak ("VMPS", VitaPack layout, LZ4 by vita/tools/lz4pack.py):
//   RegionKey   per region: the classic location names and map table (before WorldDataReplacement adds locations; the lookups are
//               rebuilt here as ReadMapNames/ReadMapTable build them)
//   Key         per classic location: the DFLocation ReadLocation builds (VitaFields), before climate/politic (MapsFile reads those
//               from the PAK buffers live: TerrainHelper changes the climate buffer)
//   PakKey      per PAK file: the unrolled 1001x500 buffer
// Miss: "VITANATIVE miss map <region> <location|region>" / "VITANATIVE miss pak <name>", the file is read as upstream.
#if UNITY_PSP2
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using DaggerfallConnect;

public static class VitaMaps
{
    public const int PackMagic = 0x53504D56;    // "VMPS"
    const int Magic = 0x52504D56;               // "VMPR", each record

    public static Action<uint, byte[]> BakeSink;     // VitaMapBaker

    static VitaPack pack;
    static readonly HashSet<uint> missed = new HashSet<uint>();

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "maps.pak"); }
    }

    public static uint Key(int region, int location)
    {
        return (uint)(region << 16 | location);
    }

    public static uint RegionKey(int region)
    {
        return (uint)(region << 16 | 0xFFFF);
    }

    public static uint PakKey(string fileName)
    {
        return string.Equals(fileName, "CLIMATE.PAK", StringComparison.OrdinalIgnoreCase) ? 0xFFFF0000 : 0xFFFF0001;
    }

    /// <summary>Fills the region's LocationCount, MapNames, MapTable and both lookups; false if not baked. empty: MAPS.BSA has no
    /// records for it (LoadRegion refuses it).</summary>
    public static bool ReadRegion(int region, ref DFRegion dfRegion, out bool empty)
    {
        byte[] bytes = Open(RegionKey(region));
        empty = bytes != null && IsEmptyRecord(bytes, region);
        return bytes != null && !empty && ReadRegionRecord(bytes, region, ref dfRegion);
    }

    static bool IsEmptyRecord(byte[] bytes, int region)
    {
        var baked = Parse<DFRegion>(bytes, RegionKey(region));
        return baked.MapNames != null && baked.MapNames.Length == 0;
    }

    /// <summary>ReadRegion from the record's bytes (also VitaMapBaker's check).</summary>
    public static bool ReadRegionRecord(byte[] bytes, int region, ref DFRegion dfRegion)
    {
        var baked = Parse<DFRegion>(bytes, RegionKey(region));
        if (baked.MapNames == null || baked.MapTable == null || baked.MapNames.Length != baked.MapTable.Length)
        {
            Miss(RegionKey(region));
            return false;
        }
        dfRegion.LocationCount = (uint)baked.MapNames.Length;
        dfRegion.MapNames = baked.MapNames;
        dfRegion.MapTable = baked.MapTable;
        dfRegion.MapNameLookup = new Dictionary<string, int>();
        dfRegion.MapIdLookup = new Dictionary<int, int>();
        for (int i = 0; i < baked.MapNames.Length; i++)
        {
            if (!dfRegion.MapNameLookup.ContainsKey(baked.MapNames[i]))
                dfRegion.MapNameLookup.Add(baked.MapNames[i], i);
            if (!dfRegion.MapIdLookup.ContainsKey(baked.MapTable[i].MapId))
                dfRegion.MapIdLookup.Add(baked.MapTable[i].MapId, i);
        }
        return true;
    }

    /// <summary>The classic location as ReadLocation parses it (climate/politic not set); false if not baked.</summary>
    public static bool ReadLocation(int region, int location, ref DFLocation dfLocation)
    {
        byte[] bytes = Open(Key(region, location));
        if (bytes == null)
            return false;
        DFLocation baked = Parse<DFLocation>(bytes, Key(region, location));
        if (!baked.Loaded)
        {
            Miss(Key(region, location));
            return false;
        }
        dfLocation = baked;
        return true;
    }

    /// <summary>Fills a PAK file's unrolled buffer; false if not baked.</summary>
    public static bool ReadPak(string fileName, byte[] buffer)
    {
        uint key = PakKey(fileName);
        byte[] bytes = Open(key);
        byte[] baked = bytes != null ? Parse<byte[]>(bytes, key) : null;
        if (baked == null || baked.Length != buffer.Length)
        {
            if (bytes != null)
                Miss(key);
            return false;
        }
        Buffer.BlockCopy(baked, 0, buffer, 0, buffer.Length);
        return true;
    }

    // The record's bytes, or null (no pack: silent; not baked: logged)
    static byte[] Open(uint key)
    {
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        if (pack.Count == 0)
            return null;
        IntPtr memory = IntPtr.Zero, data;
        int length;
        try
        {
            if (!pack.Read(key, out memory, out data, out length))
            {
                Miss(key);
                return null;
            }
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, length);
            return bytes;
        }
        finally
        {
            if (memory != IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    static void Miss(uint key)
    {
        if (!missed.Add(key))
            return;
        if (key >= 0xFFFF0000)
            Debug.LogFormat("VITANATIVE miss pak {0}", key == 0xFFFF0000 ? "CLIMATE.PAK" : "POLITIC.PAK");
        else
            Debug.LogFormat("VITANATIVE miss map {0} {1}", key >> 16, (key & 0xFFFF) == 0xFFFF ? "region" : (key & 0xFFFF).ToString());
    }

    /// <summary>A record as the readers decode it (also VitaMapBaker's check): default(T) if damaged.</summary>
    public static T Parse<T>(byte[] bytes, uint key)
    {
        try
        {
            using (var reader = new BinaryReader(new MemoryStream(bytes)))
                return reader.ReadInt32() == Magic && reader.ReadUInt32() == key ? (T)VitaFields.Read(reader, typeof(T)) : default(T);
        }
        catch (IOException)
        {
            return default(T);
        }
    }

    /// <summary>Bake (editor): a region's classic names and map table.</summary>
    public static void SaveRegion(int region, string[] mapNames, DFRegion.RegionMapTable[] mapTable)
    {
        var record = new DFRegion();
        record.MapNames = mapNames;
        record.MapTable = mapTable;
        Save(RegionKey(region), typeof(DFRegion), record);
    }

    /// <summary>Bake (editor): a region MAPS.BSA has no records for.</summary>
    public static void SaveEmptyRegion(int region)
    {
        SaveRegion(region, new string[0], new DFRegion.RegionMapTable[0]);
    }

    /// <summary>Bake (editor): one classic location.</summary>
    public static void SaveLocation(int region, int location, DFLocation dfLocation)
    {
        Save(Key(region, location), typeof(DFLocation), dfLocation);
    }

    /// <summary>Bake (editor): a PAK file's unrolled buffer.</summary>
    public static void SavePak(string fileName, byte[] buffer)
    {
        Save(PakKey(fileName), typeof(byte[]), buffer);
    }

    static void Save(uint key, Type type, object value)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(key);
            VitaFields.Write(writer, type, value);
            writer.Flush();
            BakeSink(key, stream.ToArray());
        }
    }
}
#endif
