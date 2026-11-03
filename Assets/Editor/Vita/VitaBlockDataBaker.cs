// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// BLOCKS.BSA record baker (runtime side in Assets/Scripts/Vita/VitaBlockData.cs): DFU's own BlocksFile.LoadBlock parses every record
// (mods and world data off: whole-block replacements are read before LoadBlock at run time, and per-building replacements are applied
// blockdata.pak. Check per record: the Vita's decoder reads it back and writing what it decoded gives the same bytes. Editor only.
//   vita/tools/bake_blockdata.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output .pak, both Windows paths)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;

public static class VitaBlockDataBaker
{
    public static void Bake()
    {
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        DaggerfallUnity.Settings.AssetInjection = false;
        var file = new BlocksFile(Path.Combine(arena2, BlocksFile.Filename), FileUsage.UseMemory, true);

        var records = new SortedDictionary<uint, byte[]>();
        int left = 0, bad = 0;
        for (int block = 0; block < file.Count; block++)
        {
            string name = file.GetBlockName(block);
            if (!file.LoadBlock(block))
            {
                Debug.LogFormat("VITABAKE blockdata {0} ({1}): LoadBlock failed, left to the file", block, name);
                left++;
                continue;
            }
            uint key = VitaPack.NameKey(name);
            if (records.ContainsKey(key))
            {
                Debug.LogFormat("VITABAKE failed: key {0:x8} twice ({1})", key, name);
                return;
            }
            byte[] record = VitaBlockData.Save(name, file.GetBlock(block));
            object decoded = VitaBlockData.Parse(record, key);
            if (decoded == null || !VitaBlockData.Save(name, (DFBlock)decoded).SequenceEqual(record))
            {
                Debug.LogFormat("VITABAKE blockdata {0}: decoded block differs", name);
                bad++;
            }
            records[key] = record;
        }
        long total = VitaPackWriter.Write(output, VitaBlockData.PackMagic, records);
        Debug.LogFormat("VITABAKE done: {0} blocks, {1} left, {2} differ, {3:F1} MB -> {4}", records.Count, left, bad, total / 1048576.0, output);
    }
}
