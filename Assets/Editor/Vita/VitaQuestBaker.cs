// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Quest split baker (runtime side in Assets/Scripts/Vita/VitaQuests.cs): every StreamingAssets/Quests/*.txt split as Parser.Parse's first
// pass and ParseQRC do it (Split below follows them line for line, with DFU's own SplitField/GetFieldStringValue and the static messages
// table), into StreamingAssets/VitaNative/quests.pak. Files Parse would reject (no name/QRC/QBN, a bad field or message header) aren't
// baked. Check per file: DFU's own ParseQRC (by reflection, the table set on an editor QuestMachine) and the Vita's path (record decoded,
// Message per block) give the same messages, compared through Message.GetSaveData. Editor only.
//   vita/tools/bake_quests.sh   (VITA_BAKE_OUT = output .pak, a Windows path)

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Utility;

public static class VitaQuestBaker
{
    const StringComparison comparison = StringComparison.InvariantCultureIgnoreCase;    // Parser's

    public static void Bake()
    {
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        string tables = Path.Combine(Application.streamingAssetsPath, "Tables");
        var staticMessages = new Table(File.ReadAllLines(Path.Combine(tables, "Quests-StaticMessages.txt")));
        typeof(QuestMachine).GetField("staticMessagesTable", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(QuestMachine.Instance, staticMessages);
        MethodInfo parseQrc = typeof(Parser).GetMethod("ParseQRC", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo messagesField = typeof(Quest).GetField("messages", BindingFlags.Instance | BindingFlags.NonPublic);

        var records = new SortedDictionary<uint, byte[]>();
        byte[] last = null;
        VitaQuests.BakeSink = (key, bytes) => records[key] = last = bytes;
        int quests = 0, rejected = 0, bad = 0;
        foreach (string path in Directory.GetFiles(Path.Combine(Application.streamingAssetsPath, "Quests"), "*.txt"))
        {
            string name = Path.GetFileName(path);
            string[] source = File.ReadAllLines(path);
            List<string> qrcLines;
            string reason;
            VitaQuests.Split split = Split(source, staticMessages, out qrcLines, out reason);
            if (split == null)
            {
                Debug.LogFormat("VITABAKE quest {0}: not a quest Parse accepts ({1}), left to the file", name, reason);
                rejected++;
                continue;
            }
            VitaQuests.Save(name, split);

            // DFU's ParseQRC on the same QRC lines vs Messages from the decoded record
            VitaQuests.Split decoded = VitaQuests.Parse(last, VitaPack.NameKey(name));
            var upstream = new Quest();
            var vita = new Quest();
            bool same = decoded != null && decoded.QuestName == split.QuestName && decoded.DisplayName == split.DisplayName &&
                string.Join("\n", decoded.QbnLines) == string.Join("\n", split.QbnLines);
            try
            {
                parseQrc.Invoke(new Parser(), new object[] { upstream, qrcLines });
                if (same)
                    for (int m = 0; m < decoded.MessageIds.Length; m++)
                        vita.AddMessage(decoded.MessageIds[m], new Message(vita, decoded.MessageIds[m], decoded.MessageLines[m]));
                var a = (Dictionary<int, Message>)messagesField.GetValue(upstream);
                var b = (Dictionary<int, Message>)messagesField.GetValue(vita);
                same = same && a.Count == b.Count;
                foreach (KeyValuePair<int, Message> message in a)
                {
                    Message other;
                    if (!same || !b.TryGetValue(message.Key, out other) ||
                        string.Join("\n", message.Value.GetSaveData().lines) != string.Join("\n", other.GetSaveData().lines))
                    {
                        same = false;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogFormat("VITABAKE quest {0}: check failed: {1}", name, e.InnerException != null ? e.InnerException.Message : e.Message);
                same = false;
            }
            if (!same)
            {
                Debug.LogFormat("VITABAKE quest {0}: decoded split differs from DFU's", name);
                bad++;
            }
            quests++;
        }
        VitaQuests.BakeSink = null;
        long total = VitaPackWriter.Write(output, VitaQuests.PackMagic, records);
        Debug.LogFormat("VITABAKE done: {0} quests, {1} rejected (left to the file), {2} differ, {3:F1} MB -> {4}", quests, rejected, bad,
            total / 1048576.0, output);
    }

    // Parser.Parse's first pass, then ParseQRC's block split; null (with the reason) where Parse would throw
    static VitaQuests.Split Split(string[] source, Table staticMessages, out List<string> qrcLines, out string reason)
    {
        var split = new VitaQuests.Split();
        qrcLines = new List<string>();
        var qbnLines = new List<string>();
        bool inQRC = false, inQBN = false;
        try
        {
            foreach (string line in source)
            {
                string text = line.Trim();
                if (text.StartsWith("quest:", comparison))
                    split.QuestName = Parser.GetFieldStringValue(text);
                else if (text.StartsWith("displayname:", comparison))
                    split.DisplayName = Parser.GetFieldStringValue(text);
                else if (text.StartsWith("qrc:", comparison))
                {
                    inQRC = true;
                    inQBN = false;
                    continue;
                }
                else if (text.StartsWith("qbn:", comparison))
                {
                    inQRC = false;
                    inQBN = true;
                    continue;
                }

                if (inQRC)
                    qrcLines.Add(line);
                else if (inQBN && !text.StartsWith("-", comparison))
                    qbnLines.Add(line);
            }
            if (string.IsNullOrEmpty(split.QuestName) || qrcLines.Count == 0 || qbnLines.Count == 0)
            {
                reason = "no name, QRC or QBN";
                return null;
            }

            var ids = new List<int>();
            var blocks = new List<string[]>();
            for (int i = 0; i < qrcLines.Count; i++)
            {
                if (qrcLines[i].StartsWith("-", comparison) || string.IsNullOrEmpty(qrcLines[i].Trim()))
                    continue;
                string[] parts = Parser.SplitField(qrcLines[i]);
                if (!staticMessages.HasValue(parts[0]))
                {
                    reason = "bad message block near " + qrcLines[i];
                    return null;
                }
                int messageID;
                if (parts[1].StartsWith("[") && parts[1].EndsWith("]"))
                {
                    messageID = staticMessages.GetInt("id", parts[0]);
                    if (messageID == -1)
                    {
                        reason = "static message id";
                        return null;
                    }
                }
                else if (!int.TryParse(parts[1], out messageID))
                {
                    reason = "message id " + parts[1];
                    return null;
                }
                var messageLines = new List<string>();
                while (i + 1 < qrcLines.Count)
                {
                    string text = qrcLines[++i].TrimEnd('\r');
                    if (string.IsNullOrEmpty(text))
                    {
                        if (!PeekMessageEnd(qrcLines, i))
                        {
                            messageLines.Add(" ");
                            continue;
                        }
                        break;
                    }
                    messageLines.Add(text);
                }
                ids.Add(messageID);
                blocks.Add(messageLines.ToArray());
            }
            split.MessageIds = ids.ToArray();
            split.MessageLines = blocks.ToArray();
            split.QbnLines = qbnLines.ToArray();
            reason = null;
            return split;
        }
        catch (Exception e)
        {
            reason = e.Message;
            return null;
        }
    }

    // Parser.PeekMessageEnd
    static bool PeekMessageEnd(List<string> lines, int line)
    {
        if (line + 1 >= lines.Count)
            return true;
        string nextLine = lines[line + 1];
        return nextLine.Contains(":") || nextLine.StartsWith("-") || string.IsNullOrEmpty(nextLine.Trim());
    }
}
