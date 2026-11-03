// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked -LOC book texts. LocalizedBook.OpenLocalizedBookFile read and parsed
// StreamingAssets/Text/Books/BOK#####-LOC.txt (File.Exists + ReadAllLines + tag lines) for every book title shown (ItemHelper item names),
// the %ba macro and the book reader. Assets/Editor/Vita/VitaLocBookBaker.cs runs DFU's own parser on the PC into
// StreamingAssets/VitaNative/locbooks.pak ("VLOC", VitaPack layout, LZ4 by vita/tools/lz4pack.py): per file (key = VitaPack.NameKey) each
// tag field the file sets (a field without its line is left as it was, as upstream) and the content. Mod book files still come first.
// Miss: "VITANATIVE miss locbook <name>" (a damaged record; a name not in the pack has no file).
#if UNITY_PSP2
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using DaggerfallWorkshop.Game;

public static class VitaLocBooks
{
    public const int PackMagic = 0x434F4C56;    // "VLOC"
    const int Magic = 0x524F4C56;               // "VLOR", each record

    public static System.Action<uint, byte[]> BakeSink;     // VitaLocBookBaker

    static VitaPack pack;

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "locbooks.pak"); }
    }

    static bool Ready()
    {
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        return pack.Count > 0;
    }

    /// <summary>Whether a -LOC file exists (the bake took them all), or null without a pack.</summary>
    public static bool? Exists(string filename)
    {
        return Ready() ? pack.Contains(VitaPack.NameKey(filename)) : (bool?)null;
    }

    /// <summary>Sets book's fields from the baked file: true; false if there is no such file; null without a pack or on a damaged record.</summary>
    public static bool? Apply(LocalizedBook book, string filename)
    {
        if (!Ready())
            return null;
        uint key = VitaPack.NameKey(filename);
        if (!pack.Contains(key))
            return false;
        System.IntPtr memory = System.IntPtr.Zero, data;
        int length;
        try
        {
            byte[] bytes = null;
            if (pack.Read(key, out memory, out data, out length))
            {
                bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, length);
            }
            if (bytes == null || !Parse(bytes, key, book))
            {
                Debug.LogFormat("VITANATIVE miss locbook {0}", filename);
                return null;
            }
            return true;
        }
        finally
        {
            if (memory != System.IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>Applies a record to book as Apply does (also VitaLocBookBaker's check); false if damaged.</summary>
    public static bool Parse(byte[] bytes, uint key, LocalizedBook book)
    {
        try
        {
            using (var reader = new BinaryReader(new MemoryStream(bytes)))
            {
                if (reader.ReadInt32() != Magic || reader.ReadUInt32() != key)
                    return false;
                if (reader.ReadBoolean()) book.Title = reader.ReadString();
                if (reader.ReadBoolean()) book.Author = reader.ReadString();
                if (reader.ReadBoolean()) book.IsNaughty = reader.ReadBoolean();
                if (reader.ReadBoolean()) book.Price = reader.ReadInt32();
                if (reader.ReadBoolean()) book.IsUnique = reader.ReadBoolean();
                if (reader.ReadBoolean()) book.WhenVarSet = reader.ReadString();
                book.Content = reader.ReadString();
                return true;
            }
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Bake (editor): one file's record; a null (or nullable without value) field has no line in the file.</summary>
    public static byte[] Save(string filename, string title, string author, bool? isNaughty, int? price, bool? isUnique, string whenVarSet, string content)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(VitaPack.NameKey(filename));
            writer.Write(title != null); if (title != null) writer.Write(title);
            writer.Write(author != null); if (author != null) writer.Write(author);
            writer.Write(isNaughty.HasValue); if (isNaughty.HasValue) writer.Write(isNaughty.Value);
            writer.Write(price.HasValue); if (price.HasValue) writer.Write(price.Value);
            writer.Write(isUnique.HasValue); if (isUnique.HasValue) writer.Write(isUnique.Value);
            writer.Write(whenVarSet != null); if (whenVarSet != null) writer.Write(whenVarSet);
            writer.Write(content);
            writer.Flush();
            byte[] record = stream.ToArray();
            if (BakeSink != null)
                BakeSink(VitaPack.NameKey(filename), record);
            return record;
        }
    }
}
#endif
