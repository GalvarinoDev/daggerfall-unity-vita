// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Classic book baker (runtime side in Assets/Scripts/Vita/VitaBooks.cs): every ARENA2/BOOKS/BOK*.TXT read with DFU's own BookFile (header
// fields from its ReadHeader without the price draw) into StreamingAssets/VitaNative/books.pak. Checks per book: the record decodes to the
// same header, and the Vita's draw (DFRandom.Seed = first 4 bytes, random_range_inclusive) gives upstream OpenBook's price and leaves DFRandom
// in the same state. Editor only.
//   vita/tools/bake_books.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output .pak, both Windows paths)

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;

public static class VitaBookBaker
{
    public static void Bake()
    {
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        FieldInfo headerField = typeof(BookFile).GetField("header", BindingFlags.Instance | BindingFlags.NonPublic);
        var records = new SortedDictionary<uint, byte[]>();
        byte[] last = null;
        VitaBooks.BakeSink = (key, bytes) => records[key] = last = bytes;
        int books = 0, left = 0, bad = 0;
        foreach (string path in Directory.GetFiles(Path.Combine(arena2, "BOOKS")))
        {
            string name = Path.GetFileName(path).ToUpperInvariant();
            if (!name.StartsWith("BOK") || !name.EndsWith(".TXT"))
                continue;
            byte[] file = File.ReadAllBytes(path);

            // Upstream: OpenBook from ARENA2, its price and the DFRandom state after it
            var upstream = new BookFile();
            DFRandom.Seed = 12345;
            if (!upstream.OpenBook(arena2, name))
            {
                Debug.LogFormat("VITABAKE book {0}: not readable, left to the runtime path", name);
                left++;
                continue;
            }
            int upstreamPrice = upstream.Price;
            uint upstreamState = DFRandom.Seed;

            // The header without the draw (OpenBook(byte[]) with a non-BOK name skips it)
            var raw = new BookFile();
            raw.OpenBook(file, "RAW");
            var h = (BookFile.BookHeader)headerField.GetValue(raw);
            var book = new VitaBooks.Book
            {
                Title = h.Title, Author = h.Author, IsNaughty = h.IsNaughty, Price = h.Price, Seed = BitConverter.ToUInt32(file, 0),
                Unknown1 = h.Unknown1, Unknown2 = h.Unknown2, Unknown3 = h.Unknown3, PageOffsets = h.PageOffsets, NullValues = h.NullValues, File = file,
            };
            VitaBooks.Save(name, book);

            // The Vita's path: decode the record, draw as BookFile.OpenBook's hook does
            VitaBooks.Book decoded = VitaBooks.Parse(last, VitaPack.NameKey(name));
            DFRandom.Seed = 12345;
            DFRandom.Seed = decoded.Seed;
            int vitaPrice = DFRandom.random_range_inclusive(300, 800);
            if (decoded.Title != upstream.Title || decoded.Author != upstream.Author || decoded.IsNaughty != upstream.IsNaughty ||
                decoded.PageOffsets.Length != upstream.PageCount || vitaPrice != upstreamPrice || DFRandom.Seed != upstreamState ||
                Convert.ToBase64String(decoded.File) != Convert.ToBase64String(file))
            {
                Debug.LogFormat("VITABAKE book {0}: decoded record differs", name);
                bad++;
            }
            books++;
        }
        VitaBooks.BakeSink = null;
        long total = VitaPackWriter.Write(output, VitaBooks.PackMagic, records);
        Debug.LogFormat("VITABAKE done: {0} books, {1} left, {2} differ, {3:F1} MB -> {4}", books, left, bad, total / 1048576.0, output);
    }
}
