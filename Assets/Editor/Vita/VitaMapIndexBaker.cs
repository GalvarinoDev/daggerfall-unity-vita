// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Location index baker (runtime side and layout in Assets/Scripts/Vita/VitaMapIndex.cs). Runs DFU's own ContentReader.EnumerateMaps on
// the PC (mods off), writes its two dictionaries into StreamingAssets/VitaNative/maps.bin, then reads the file back with the Vita loader
// and compares every entry. Editor only.
//   vita/tools/bake_maps.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output file, both Windows paths)

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;

public static class VitaMapIndexBaker
{
    public static void Bake()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        if (string.IsNullOrEmpty(output))
            output = VitaMapIndex.PackPath;

        DaggerfallUnity.Settings.AssetInjection = false;
        DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
        dfUnity.ChangeArena2Path(arena2);
        if (!dfUnity.IsReady)
        {
            Debug.Log("VITABAKE failed: ARENA2 not valid: " + arena2);
            return;
        }

        // DFU's own EnumerateMaps (private), never an older maps.bin
        VitaMapIndex.Disabled = true;
        ContentReader reader = dfUnity.ContentReader;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ContentReader).GetMethod("EnumerateMaps", flags).Invoke(reader, null);
        var mapDict = (Dictionary<int, ContentReader.MapSummary>)typeof(ContentReader).GetField("mapDict", flags).GetValue(reader);
        var locationIds = (Dictionary<int, int>)typeof(ContentReader).GetField("locationIdToMapIdDict", flags).GetValue(reader);

        Directory.CreateDirectory(Path.GetDirectoryName(output));
        VitaMapIndex.Write(output, mapDict, locationIds);

        // Read it back the way the Vita does and compare
        var readMaps = new Dictionary<int, ContentReader.MapSummary>();
        var readIds = new Dictionary<int, int>();
        VitaMapIndex.Disabled = false;
        bool loaded = VitaMapIndex.TryLoad(readMaps, readIds, output);
        int bad = 0;
        foreach (KeyValuePair<int, ContentReader.MapSummary> pair in mapDict)
        {
            ContentReader.MapSummary s;
            if (!readMaps.TryGetValue(pair.Key, out s) || !s.Equals(pair.Value))
                bad++;
        }
        foreach (KeyValuePair<int, int> pair in locationIds)
        {
            int id;
            if (!readIds.TryGetValue(pair.Key, out id) || id != pair.Value)
                bad++;
        }
        if (!loaded || readMaps.Count != mapDict.Count || readIds.Count != locationIds.Count)
            bad++;
        Debug.LogFormat("VITABAKE {0}: {1} locations, {2} location ids, {3:F0} KB, {4} mismatches, {5:F1} s -> {6}", bad == 0 ? "done" : "FAILED",
            mapDict.Count, locationIds.Count, new FileInfo(output).Length / 1024.0, bad, watch.Elapsed.TotalSeconds, output);
        if (bad != 0)
            File.Delete(output);
    }
}
