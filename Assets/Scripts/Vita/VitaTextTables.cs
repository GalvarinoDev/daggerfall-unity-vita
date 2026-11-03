// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked English string tables. TextProvider.GetVitaStringTable parsed each collection's CSV text (master CSV,
// Vita additions, StreamingAssets/Text patch) on first use: Internal_RSC (TEXT.RSC's strings) and Internal_Locations are ~430 KB of CSV each.
// Assets/Editor/Vita/VitaTextTableBaker.cs runs that same loader on the PC into StreamingAssets/VitaNative/strings.pak ("VSTB", VitaPack
// layout, LZ4 by vita/tools/lz4pack.py): per collection (key = VitaPack.NameKey) the merged table as key/value pairs.
// Miss: "VITANATIVE miss strings <collection>", the CSVs are parsed as upstream. Built-in mods ship no CSVs (a mod CSV would need a re-bake).
#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public static class VitaTextTables
{
    public const int PackMagic = 0x42545356;    // "VSTB"
    const int Magic = 0x52545356;               // "VSTR", each record

    public static System.Action<uint, byte[]> BakeSink;     // VitaTextTableBaker

    static VitaPack pack;

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "strings.pak"); }
    }

    /// <summary>The baked table of a collection (empty if it has no CSV), or null: no pack, or a damaged record (logged).</summary>
    public static VitaStringTable Read(string collection)
    {
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        uint key = VitaPack.NameKey(collection);
        if (pack.Count > 0 && !pack.Contains(key))
            return new VitaStringTable();    // the bake took every CSV there is: this collection has none (e.g. Internal_Quests)
        System.IntPtr memory = System.IntPtr.Zero, data;
        int length;
        try
        {
            VitaStringTable table = null;
            if (pack.Read(key, out memory, out data, out length) && length >= 12)
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, length);
                if (System.BitConverter.ToInt32(bytes, 0) == Magic && System.BitConverter.ToUInt32(bytes, 4) == key)
                    table = VitaStringTable.FromRows(bytes, 12, System.BitConverter.ToInt32(bytes, 8));    // no objects per row
            }
            if (table == null && pack.Count > 0)
                Debug.LogFormat("VITANATIVE miss strings {0}", collection);
            return table;
        }
        finally
        {
            if (memory != System.IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>A record as Read decodes it (also VitaTextTableBaker's check): null if damaged.</summary>
    public static Dictionary<string, string> Parse(byte[] bytes, uint key)
    {
        try
        {
            using (var reader = new BinaryReader(new MemoryStream(bytes)))
            {
                if (reader.ReadInt32() != Magic || reader.ReadUInt32() != key)
                    return null;
                int count = reader.ReadInt32();
                var table = new Dictionary<string, string>(count);
                for (int i = 0; i < count; i++)
                {
                    string name = reader.ReadString();
                    table[name] = reader.ReadBoolean() ? reader.ReadString() : null;
                }
                return table;
            }
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Bake (editor): one collection's record.</summary>
    public static void Save(string collection, Dictionary<string, string> table)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(VitaPack.NameKey(collection));
            writer.Write(table.Count);
            foreach (KeyValuePair<string, string> row in table)
            {
                writer.Write(row.Key);
                writer.Write(row.Value != null);
                if (row.Value != null)
                    writer.Write(row.Value);
            }
            writer.Flush();
            BakeSink(VitaPack.NameKey(collection), stream.ToArray());
        }
    }
}
#endif
