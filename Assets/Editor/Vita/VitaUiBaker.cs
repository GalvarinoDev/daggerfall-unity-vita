// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Vita menu baker (runtime side in Assets/Scripts/Vita/VitaUiPack.cs): the quick menu's rings for 1-8 slices, its stone bars, tab and
// button icons, and the HUD style box's stones, drawn by the menus' own code (VitaQuickMenuWindow.BakeRecords) from ARENA2, into
// StreamingAssets/VitaNative/vitaui.pak. Editor only.
//   vita/tools/bake_ui.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output .pak, both Windows paths)

using System;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop;

public static class VitaUiBaker
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
        var records = new SortedDictionary<uint, byte[]>();
        var names = new Dictionary<uint, string>();
        bool clash = false;
        VitaQuickMenuWindow.BakeRecords((name, bytes) =>
        {
            uint key = VitaPack.NameKey(name);
            if (names.ContainsKey(key))
            {
                Debug.LogFormat("VITABAKE failed: {0} and {1} share key {2:x8}", name, names[key], key);
                clash = true;
            }
            names[key] = name;
            records[key] = bytes;
            Debug.LogFormat("VITABAKE ui {0}: {1} bytes", name, bytes.Length);
        });
        if (clash)
            return;
        long size = VitaPackWriter.Write(output, VitaUiPack.Magic, records);
        Debug.LogFormat("VITABAKE done: {0} records, {1} bytes -> {2}", records.Count, size, output);
    }
}
