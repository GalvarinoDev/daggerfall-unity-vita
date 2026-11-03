// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// String table baker (runtime side in Assets/Scripts/Vita/VitaTextTables.cs): every collection with a CSV in StreamingAssets/Text (master
// CSV folder, Vita/, patches) loaded by the Vita's own TextProvider.LoadVitaCsvTable, into StreamingAssets/VitaNative/strings.pak. Each
// record is decoded again with VitaTextTables.Parse and compared row by row before it is written. Editor only.
//   vita/tools/bake_strings.sh   (VITA_BAKE_OUT = output .pak, a Windows path)

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallWorkshop.Utility;

public static class VitaTextTableBaker
{
    public static void Bake()
    {
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        string text = Path.Combine(Application.streamingAssetsPath, "Text");
        var collections = new SortedSet<string>();
        foreach (string folder in new[] { Path.Combine(text, "Master Localization CSV Files"), Path.Combine(text, "Vita"), text })
            foreach (string file in Directory.GetFiles(folder, "*.csv"))
                collections.Add(Path.GetFileNameWithoutExtension(file));

        var records = new SortedDictionary<uint, byte[]>();
        byte[] last = null;
        VitaTextTables.BakeSink = (key, bytes) => records[key] = last = bytes;
        int rows = 0, bad = 0;
        foreach (string collection in collections)
        {
            Dictionary<string, string> table = TextProvider.LoadVitaCsvTable(collection);
            VitaTextTables.Save(collection, table);
            Dictionary<string, string> decoded = VitaTextTables.Parse(last, VitaPack.NameKey(collection));
            bool same = decoded != null && decoded.Count == table.Count;
            if (same)
                foreach (KeyValuePair<string, string> row in table)
                {
                    string value;
                    if (!decoded.TryGetValue(row.Key, out value) || value != row.Value)
                    {
                        same = false;
                        break;
                    }
                }
            if (!same)
            {
                Debug.LogFormat("VITABAKE strings {0}: decoded table differs", collection);
                bad++;
            }
            rows += table.Count;
            Debug.LogFormat("VITABAKE strings {0}: {1} rows", collection, table.Count);
        }
        VitaTextTables.BakeSink = null;
        long total = VitaPackWriter.Write(output, VitaTextTables.PackMagic, records);
        Debug.LogFormat("VITABAKE done: {0} collections, {1} rows, {2} differ, {3:F1} MB -> {4}", collections.Count, rows, bad, total / 1048576.0, output);
    }
}
