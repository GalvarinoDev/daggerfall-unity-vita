// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// MAPS.BSA / CLIMATE.PAK / POLITIC.PAK baker (runtime side and layout in Assets/Scripts/Vita/VitaMaps.cs): DFU's own MapsFile reads
// every region (mods and world data overrides off: WorldDataReplacement adds its locations at run time) and its private ReadLocation
// parses every classic location; PakFile unrolls both PAKs. Into StreamingAssets/VitaNative/maps.pak. Check per record: the Vita's
// decoder reads it back and writing what it decoded gives the same bytes; region names/tables also compared field by field. Editor only.
//   vita/tools/bake_mapdata.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output .pak, both Windows paths)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;

public static class VitaMapBaker
{
    public static void Bake()
    {
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        DaggerfallUnity.Settings.AssetInjection = false;
        DaggerfallUnity.Instance.ChangeArena2Path(arena2);
        if (!DaggerfallUnity.Instance.IsReady)
        {
            Debug.Log("VITABAKE failed: ARENA2 not valid: " + arena2);
            return;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var maps = new MapsFile(Path.Combine(arena2, MapsFile.Filename), FileUsage.UseMemory, true);
        MethodInfo readLocation = typeof(MapsFile).GetMethod("ReadLocation", flags);
        MethodInfo readLocationCount = typeof(MapsFile).GetMethod("ReadLocationCount", flags);

        var records = new SortedDictionary<uint, byte[]>();
        byte[] last = null;
        VitaMaps.BakeSink = (key, bytes) => records[key] = last = bytes;
        int regions = 0, empty = 0, locations = 0, left = 0, bad = 0;
        for (int region = 0; region < maps.RegionCount; region++)
        {
            if (!maps.LoadRegion(region))
            {
                Debug.LogFormat("VITABAKE map region {0}: no records (LoadRegion refuses it on the Vita too)", region);
                VitaMaps.SaveEmptyRegion(region);   // the Vita then refuses it without a MAPS.BSA read
                empty++;
                continue;
            }
            DFRegion dfRegion = maps.GetRegion(region);
            int count = (int)(uint)readLocationCount.Invoke(maps, new object[] { region });
            if (dfRegion.MapNames.Length != count || dfRegion.MapTable.Length != count)
            {
                Debug.LogFormat("VITABAKE failed: region {0} has added locations ({1} vs {2}): world data must be off", region,
                    dfRegion.MapNames.Length, count);
                return;
            }
            VitaMaps.SaveRegion(region, dfRegion.MapNames, dfRegion.MapTable);
            var decoded = new DFRegion();
            if (!VitaMaps.ReadRegionRecord(last, region, ref decoded) || !decoded.MapNames.SequenceEqual(dfRegion.MapNames) ||
                !decoded.MapTable.SequenceEqual(dfRegion.MapTable) || !SameLookups(decoded, dfRegion))
            {
                Debug.LogFormat("VITABAKE map region {0}: decoded region differs", region);
                bad++;
            }
            regions++;

            for (int location = 0; location < count; location++)
            {
                var args = new object[] { region, location, new DFLocation() };
                if (!(bool)readLocation.Invoke(maps, args))
                {
                    Debug.LogFormat("VITABAKE map {0} {1}: ReadLocation failed, left to the file", region, location);
                    left++;
                    continue;
                }
                VitaMaps.SaveLocation(region, location, (DFLocation)args[2]);
                if (!RoundTrips<DFLocation>(last, VitaMaps.Key(region, location), (key, value) => VitaMaps.SaveLocation(region, location, value)))
                {
                    Debug.LogFormat("VITABAKE map {0} {1}: decoded location differs", region, location);
                    bad++;
                }
                locations++;
            }
        }

        var paks = new[] { maps.ClimateFile, (PakFile)typeof(MapsFile).GetField("politicPak", flags).GetValue(maps) };
        var pakNames = new[] { "CLIMATE.PAK", "POLITIC.PAK" };
        for (int i = 0; i < paks.Length; i++)
        {
            VitaMaps.SavePak(pakNames[i], paks[i].Buffer);
            byte[] decoded = VitaMaps.Parse<byte[]>(last, VitaMaps.PakKey(pakNames[i]));
            if (decoded == null || !decoded.SequenceEqual(paks[i].Buffer))
            {
                Debug.LogFormat("VITABAKE map {0}: decoded buffer differs", pakNames[i]);
                bad++;
            }
        }
        VitaMaps.BakeSink = null;

        long total = VitaPackWriter.Write(output, VitaMaps.PackMagic, records);
        Debug.LogFormat("VITABAKE done: {0} regions ({1} empty), {2} locations, 2 paks, {3} left, {4} differ, {5:F1} MB -> {6}",
            regions, empty, locations, left, bad, total / 1048576.0, output);
    }

    // Decoding the record and writing the result again gives the same bytes (every field came back)
    static bool RoundTrips<T>(byte[] record, uint key, Action<uint, T> save)
    {
        T decoded = VitaMaps.Parse<T>(record, key);
        byte[] again = null;
        Action<uint, byte[]> sink = VitaMaps.BakeSink;
        VitaMaps.BakeSink = (k, bytes) => again = bytes;
        save(key, decoded);
        VitaMaps.BakeSink = sink;
        return again != null && again.SequenceEqual(record);
    }

    static bool SameLookups(DFRegion a, DFRegion b)
    {
        return a.MapNameLookup.Count == b.MapNameLookup.Count && a.MapIdLookup.Count == b.MapIdLookup.Count &&
            a.MapNameLookup.All(p => b.MapNameLookup.ContainsKey(p.Key) && b.MapNameLookup[p.Key] == p.Value) &&
            a.MapIdLookup.All(p => b.MapIdLookup.ContainsKey(p.Key) && b.MapIdLookup[p.Key] == p.Value);
    }
}
