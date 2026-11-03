// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Writes a version 1 VitaPack (Assets/Scripts/Vita/VitaPack.cs): magic, version, count, key/offset/length per record, records at 512-byte
// offsets; vita/tools/lz4pack.py compresses it afterwards. Editor only.

using System.Collections.Generic;
using System.IO;

public static class VitaPackWriter
{
    /// <summary>Writes the records (sorted by key) to path; returns the file size.</summary>
    public static long Write(string path, int magic, SortedDictionary<uint, byte[]> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var writer = new BinaryWriter(new FileStream(path, FileMode.Create)))
        {
            writer.Write(magic);
            writer.Write(VitaPack.Version);
            writer.Write(records.Count);
            long offset = AlignUp(12 + 12 * records.Count);
            foreach (KeyValuePair<uint, byte[]> record in records)
            {
                writer.Write(record.Key);
                writer.Write((int)offset);
                writer.Write(record.Value.Length);
                offset = AlignUp(offset + record.Value.Length);
            }
            offset = AlignUp(12 + 12 * records.Count);
            foreach (byte[] record in records.Values)
            {
                writer.Seek((int)offset, SeekOrigin.Begin);
                writer.Write(record);
                offset = AlignUp(offset + record.Length);
            }
            return writer.BaseStream.Length;
        }
    }

    static long AlignUp(long position)
    {
        return (position + 511) & ~511L;
    }
}
