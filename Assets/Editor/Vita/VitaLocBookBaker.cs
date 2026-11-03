// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// -LOC book baker (runtime side in Assets/Scripts/Vita/VitaLocBooks.cs): every StreamingAssets/Text/Books/*-LOC.txt through DFU's own
// LocalizedBook.OpenLocalizedBookFile, into StreamingAssets/VitaNative/locbooks.pak. Each file is parsed twice into books preset with two
// different values per field, so a field the file doesn't set shows up as left alone. Check: the record applied to the same two presets
// gives the same books as upstream's parse. Editor only.
//   vita/tools/bake_locbooks.sh   (VITA_BAKE_OUT = output .pak, a Windows path)

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallWorkshop.Game;

public static class VitaLocBookBaker
{
    public static void Bake()
    {
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        var records = new SortedDictionary<uint, byte[]>();
        int bad = 0, left = 0;
        foreach (string path in Directory.GetFiles(Path.Combine(Application.streamingAssetsPath, "Text/Books"), "*-LOC.txt"))
        {
            string name = Path.GetFileName(path);
            LocalizedBook a = Preset(false), b = Preset(true);
            if (!a.OpenLocalizedBookFile(name) || !b.OpenLocalizedBookFile(name))
            {
                // Exists() would say yes while Open says no: keep the file path for it
                Debug.LogFormat("VITABAKE locbook {0}: upstream refuses it, left to the file", name);
                left++;
                continue;
            }
            byte[] record = VitaLocBooks.Save(name,
                a.Title == b.Title ? a.Title : null, a.Author == b.Author ? a.Author : null,
                a.IsNaughty == b.IsNaughty ? a.IsNaughty : (bool?)null, a.Price == b.Price ? a.Price : (int?)null,
                a.IsUnique == b.IsUnique ? a.IsUnique : (bool?)null, a.WhenVarSet == b.WhenVarSet ? a.WhenVarSet : null, a.Content);
            records[VitaPack.NameKey(name)] = record;
            LocalizedBook va = Preset(false), vb = Preset(true);
            if (!VitaLocBooks.Parse(record, VitaPack.NameKey(name), va) || !VitaLocBooks.Parse(record, VitaPack.NameKey(name), vb) ||
                JsonUtility.ToJson(va) != JsonUtility.ToJson(a) || JsonUtility.ToJson(vb) != JsonUtility.ToJson(b) || Fields(va) != Fields(a) || Fields(vb) != Fields(b))
            {
                Debug.LogFormat("VITABAKE locbook {0}: decoded record differs", name);
                bad++;
            }
        }
        long total = VitaPackWriter.Write(output, VitaLocBooks.PackMagic, records);
        Debug.LogFormat("VITABAKE done: {0} books, {1} left, {2} differ, {3:F1} MB -> {4}", records.Count, left, bad, total / 1048576.0, output);
    }

    static LocalizedBook Preset(bool second)
    {
        string mark = second ? "\u0001B" : "\u0001A";
        return new LocalizedBook
        {
            Title = mark, Author = mark, IsNaughty = second, Price = second ? -22222 : -11111, IsUnique = second, WhenVarSet = mark, Content = mark,
        };
    }

    static string Fields(LocalizedBook book)
    {
        return string.Join("\u0002", new[] { book.Title, book.Author, book.IsNaughty.ToString(), book.Price.ToString(), book.IsUnique.ToString(),
            book.WhenVarSet, book.Content });
    }
}
