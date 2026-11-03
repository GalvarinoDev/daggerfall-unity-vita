// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked biography questionnaires. BiogFile's constructor read
// StreamingAssets/BIOGs/BIOG##T#.TXT and parsed its questions, answers, effects and backstory id at character creation.
// Assets/Editor/Vita/VitaBiogBaker.cs runs DFU's own BiogFile on the PC into StreamingAssets/VitaNative/biogs.pak ("VBIO", VitaPack layout,
// LZ4 by vita/tools/lz4pack.py): per file (key = VitaPack.NameKey) the backstory id, the file text (BiogFile keeps it) and every question's
// two lines and answers. BiogFile.VitaLoad (the partial part below) fills the BiogFile from it. Miss: "VITANATIVE miss biog <name>".
#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public static class VitaBiogs
{
    public const int PackMagic = 0x4F494256;    // "VBIO"
    const int Magic = 0x52494256;               // "VBIR", each record

    public static System.Action<uint, byte[]> BakeSink;     // VitaBiogBaker

    public sealed class Biog
    {
        public int BackstoryId;
        public string Text;
        public string[][] QuestionLines;                    // per question, Question.lines strings
        public string[][] AnswerTexts;                      // per question, per answer
        public string[][][] AnswerEffects;                  // per question, per answer, effects
    }

    static VitaPack pack;

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "biogs.pak"); }
    }

    /// <summary>The baked questionnaire, or null (no pack, or not baked: logged).</summary>
    public static Biog Read(string fileName)
    {
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        if (pack.Count == 0)
            return null;
        uint key = VitaPack.NameKey(fileName);
        System.IntPtr memory = System.IntPtr.Zero, data;
        int length;
        try
        {
            Biog biog = null;
            if (pack.Read(key, out memory, out data, out length))
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, length);
                biog = Parse(bytes, key);
            }
            if (biog == null)
                Debug.LogFormat("VITANATIVE miss biog {0}", fileName);
            return biog;
        }
        finally
        {
            if (memory != System.IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>A record as Read decodes it (also VitaBiogBaker's check): null if damaged.</summary>
    public static Biog Parse(byte[] bytes, uint key)
    {
        try
        {
            using (var reader = new BinaryReader(new MemoryStream(bytes)))
            {
                if (reader.ReadInt32() != Magic || reader.ReadUInt32() != key)
                    return null;
                var biog = new Biog();
                biog.BackstoryId = reader.ReadInt32();
                biog.Text = reader.ReadString();
                int questions = reader.ReadInt32();
                biog.QuestionLines = new string[questions][];
                biog.AnswerTexts = new string[questions][];
                biog.AnswerEffects = new string[questions][][];
                for (int q = 0; q < questions; q++)
                {
                    biog.QuestionLines[q] = ReadStrings(reader);
                    biog.AnswerTexts[q] = ReadStrings(reader);
                    biog.AnswerEffects[q] = new string[biog.AnswerTexts[q].Length][];
                    for (int a = 0; a < biog.AnswerTexts[q].Length; a++)
                        biog.AnswerEffects[q][a] = ReadStrings(reader);
                }
                return biog;
            }
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Bake (editor): one questionnaire's record; returns its bytes.</summary>
    public static byte[] Save(string fileName, Biog biog)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(VitaPack.NameKey(fileName));
            writer.Write(biog.BackstoryId);
            writer.Write(biog.Text);
            writer.Write(biog.QuestionLines.Length);
            for (int q = 0; q < biog.QuestionLines.Length; q++)
            {
                WriteStrings(writer, biog.QuestionLines[q]);
                WriteStrings(writer, biog.AnswerTexts[q]);
                for (int a = 0; a < biog.AnswerTexts[q].Length; a++)
                    WriteStrings(writer, biog.AnswerEffects[q][a]);
            }
            writer.Flush();
            byte[] record = stream.ToArray();
            if (BakeSink != null)
                BakeSink(VitaPack.NameKey(fileName), record);
            return record;
        }
    }

    static string[] ReadStrings(BinaryReader reader)
    {
        var strings = new string[reader.ReadInt32()];
        for (int i = 0; i < strings.Length; i++)
            strings[i] = reader.ReadBoolean() ? reader.ReadString() : null;
        return strings;
    }

    static void WriteStrings(BinaryWriter writer, string[] strings)
    {
        writer.Write(strings.Length);
        foreach (string s in strings)
        {
            writer.Write(s != null);
            if (s != null)
                writer.Write(s);
        }
    }
}

namespace DaggerfallConnect.Arena2
{
    public partial class BiogFile
    {
        /// <summary>Vita: fills this BiogFile from its baked questionnaire (VitaBiogs) as the constructor's parse does; false on a miss.</summary>
        bool VitaLoad(string fileName)
        {
            VitaBiogs.Biog biog = VitaBiogs.Read(fileName);
            if (biog == null || biog.QuestionLines.Length != questionCount)
                return false;
            questionsStr = biog.Text;
            backstoryId = biog.BackstoryId;
            for (int q = 0; q < questionCount; q++)
            {
                questions[q] = new Question();
                for (int j = 0; j < Question.lines && j < biog.QuestionLines[q].Length; j++)
                    questions[q].Text[j] = biog.QuestionLines[q][j];
                for (int a = 0; a < biog.AnswerTexts[q].Length; a++)
                {
                    var answer = new Answer();
                    answer.Text = biog.AnswerTexts[q][a];
                    answer.Effects.AddRange(biog.AnswerEffects[q][a]);
                    questions[q].Answers.Add(answer);
                }
            }
            Q1Tokens = new List<int>();
            Q2Tokens = new List<int>();
            Q3Tokens = new List<int>();
            Q4Tokens = new List<int>();
            Q5Tokens = new List<int>();
            Q6Tokens = new List<int>();
            Q7Tokens = new List<int>();
            Q8Tokens = new List<int>();
            Q9Tokens = new List<int>();
            Q10Tokens = new List<int>();
            Q11Tokens = new List<int>();
            Q12Tokens = new List<int>();
            return true;
        }
    }
}
#endif
