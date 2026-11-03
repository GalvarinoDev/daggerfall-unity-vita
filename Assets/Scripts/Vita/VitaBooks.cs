// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked classic book files. Every book made for loot, shops and quests (ItemBuilder.CreateBook/CreateRandomBook)
// and the %ba author macro opened ARENA2/BOOKS/BOK*.TXT (a folder the arena2.pak doesn't cover: a card lookup each time) and read its header.
// Assets/Editor/Vita/VitaBookBaker.cs reads each with DFU's own BookFile on the PC into StreamingAssets/VitaNative/books.pak ("VBOK",
// VitaPack layout, LZ4 by vita/tools/lz4pack.py): per file (key = VitaPack.NameKey) the header BookFile.ReadHeader reads, the first 4 bytes
// that seed its price draw, and the file bytes (page tokens, only read for the one book without a -LOC text). BookFile.OpenBook takes the
// record and makes the same seeded DFRandom draw. Miss: "VITANATIVE miss book <name>", the file is opened as upstream.
#if UNITY_PSP2
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public static class VitaBooks
{
    public const int PackMagic = 0x4B4F4256;    // "VBOK"
    const int Magic = 0x524B4256;               // "VBKR", each record

    public static System.Action<uint, byte[]> BakeSink;     // VitaBookBaker

    public sealed class Book
    {
        public string Title, Author;
        public bool IsNaughty;
        public uint Price, Seed;
        public ushort Unknown1, Unknown2, Unknown3;
        public uint[] PageOffsets;
        public byte[] NullValues, File;
    }

    static VitaPack pack;

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "books.pak"); }
    }

    /// <summary>The baked book file, or null (not baked: logged when a pack exists).</summary>
    public static Book Read(string name)
    {
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        uint key = VitaPack.NameKey(name);
        System.IntPtr memory = System.IntPtr.Zero, data;
        int length;
        try
        {
            Book book = null;
            if (pack.Read(key, out memory, out data, out length))
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, length);
                book = Parse(bytes, key);
            }
            if (book == null && pack.Count > 0)
                Debug.LogFormat("VITANATIVE miss book {0}", name);
            return book;
        }
        finally
        {
            if (memory != System.IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>A record as Read decodes it (also VitaBookBaker's check): null if damaged.</summary>
    public static Book Parse(byte[] bytes, uint key)
    {
        try
        {
            using (var reader = new BinaryReader(new MemoryStream(bytes)))
            {
                if (reader.ReadInt32() != Magic || reader.ReadUInt32() != key)
                    return null;
                var book = new Book();
                book.Title = reader.ReadString();
                book.Author = reader.ReadString();
                book.IsNaughty = reader.ReadBoolean();
                book.Price = reader.ReadUInt32();
                book.Seed = reader.ReadUInt32();
                book.Unknown1 = reader.ReadUInt16();
                book.Unknown2 = reader.ReadUInt16();
                book.Unknown3 = reader.ReadUInt16();
                book.PageOffsets = new uint[reader.ReadUInt16()];
                for (int i = 0; i < book.PageOffsets.Length; i++)
                    book.PageOffsets[i] = reader.ReadUInt32();
                book.NullValues = reader.ReadBytes(reader.ReadInt32());
                book.File = reader.ReadBytes(reader.ReadInt32());
                return book;
            }
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Bake (editor): one book's record.</summary>
    public static void Save(string name, Book book)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(VitaPack.NameKey(name));
            writer.Write(book.Title);
            writer.Write(book.Author);
            writer.Write(book.IsNaughty);
            writer.Write(book.Price);
            writer.Write(book.Seed);
            writer.Write(book.Unknown1);
            writer.Write(book.Unknown2);
            writer.Write(book.Unknown3);
            writer.Write((ushort)book.PageOffsets.Length);
            foreach (uint offset in book.PageOffsets)
                writer.Write(offset);
            writer.Write(book.NullValues.Length);
            writer.Write(book.NullValues);
            writer.Write(book.File.Length);
            writer.Write(book.File);
            writer.Flush();
            BakeSink(VitaPack.NameKey(name), stream.ToArray());
        }
    }
}
#endif
