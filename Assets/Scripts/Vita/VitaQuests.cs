// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked quest text split. Every quest start
// read its StreamingAssets/Quests file (File.Exists + ReadAllLines in a 267-file folder) and split it on the Vita: header fields, QRC/QBN
// sections, QRC message blocks. Assets/Editor/Vita/VitaQuestBaker.cs splits every quest file on the PC into StreamingAssets/VitaNative/
// quests.pak ("VQST", VitaPack layout, LZ4 by vita/tools/lz4pack.py): per file (key = VitaPack.NameKey of the file name) the quest and
// display names, each message block's id and lines, and the QBN lines. Parser.VitaParse builds the Quest from it with DFU's own Message
// and ParseQBN, so sites, NPCs and every UnityEngine.Random draw happen exactly as upstream. Files upstream rejects aren't baked (they fail
// the same way through the file). Miss: "VITANATIVE miss quest <name>".
#if UNITY_PSP2
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public static class VitaQuests
{
    public const int PackMagic = 0x54535156;    // "VQST"
    const int Magic = 0x52535156;               // "VQSR", each record

    public static System.Action<uint, byte[]> BakeSink;     // VitaQuestBaker

    public sealed class Split
    {
        public string QuestName, DisplayName;   // null where the file has no such line
        public int[] MessageIds;
        public string[][] MessageLines;
        public string[] QbnLines;
    }

    static VitaPack pack;

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "quests.pak"); }
    }

    /// <summary>The baked split of a quest file in folder, or null: not StreamingAssets/Quests, no pack, or not baked (logged).</summary>
    public static Split Read(string folder, string questName)
    {
        if (folder == null || !string.Equals(folder.TrimEnd('/', '\\'), Path.Combine(Application.streamingAssetsPath, "Quests"),
            System.StringComparison.OrdinalIgnoreCase))
            return null;
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        if (pack.Count == 0)
            return null;
        string name = questName.EndsWith(".txt") ? questName : questName + ".txt";
        uint key = VitaPack.NameKey(name);
        System.IntPtr memory = System.IntPtr.Zero, data;
        int length;
        try
        {
            Split split = null;
            if (pack.Read(key, out memory, out data, out length))
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, length);
                split = Parse(bytes, key);
            }
            if (split == null)
                Debug.LogFormat("VITANATIVE miss quest {0}", name);
            return split;
        }
        finally
        {
            if (memory != System.IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>A record as Read decodes it (also VitaQuestBaker's check): null if damaged.</summary>
    public static Split Parse(byte[] bytes, uint key)
    {
        try
        {
            using (var reader = new BinaryReader(new MemoryStream(bytes)))
            {
                if (reader.ReadInt32() != Magic || reader.ReadUInt32() != key)
                    return null;
                var split = new Split();
                split.QuestName = reader.ReadBoolean() ? reader.ReadString() : null;
                split.DisplayName = reader.ReadBoolean() ? reader.ReadString() : null;
                int count = reader.ReadInt32();
                split.MessageIds = new int[count];
                split.MessageLines = new string[count][];
                for (int m = 0; m < count; m++)
                {
                    split.MessageIds[m] = reader.ReadInt32();
                    split.MessageLines[m] = ReadLines(reader);
                }
                split.QbnLines = ReadLines(reader);
                return split;
            }
        }
        catch (IOException)
        {
            return null;
        }
    }

    static string[] ReadLines(BinaryReader reader)
    {
        var lines = new string[reader.ReadInt32()];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = reader.ReadString();
        return lines;
    }

    /// <summary>Bake (editor): one quest file's record.</summary>
    public static void Save(string fileName, Split split)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(VitaPack.NameKey(fileName));
            foreach (string value in new[] { split.QuestName, split.DisplayName })
            {
                writer.Write(value != null);
                if (value != null)
                    writer.Write(value);
            }
            writer.Write(split.MessageIds.Length);
            for (int m = 0; m < split.MessageIds.Length; m++)
            {
                writer.Write(split.MessageIds[m]);
                WriteLines(writer, split.MessageLines[m]);
            }
            WriteLines(writer, split.QbnLines);
            writer.Flush();
            BakeSink(VitaPack.NameKey(fileName), stream.ToArray());
        }
    }

    static void WriteLines(BinaryWriter writer, string[] lines)
    {
        writer.Write(lines.Length);
        foreach (string line in lines)
            writer.Write(line);
    }
}
#endif
