// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Biography baker (runtime side in Assets/Scripts/Vita/VitaBiogs.cs): every StreamingAssets/BIOGs/BIOG##T#.TXT through DFU's own BiogFile
// constructor (CharacterDocument with that class and biography index), into StreamingAssets/VitaNative/biogs.pak. Check per file: the record
// decodes to the same backstory id, text, question lines, answers and effects. Editor only.
//   vita/tools/bake_biogs.sh   (VITA_BAKE_OUT = output .pak, a Windows path)

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop.Game.Player;

public static class VitaBiogBaker
{
    public static void Bake()
    {
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        const BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo idField = typeof(BiogFile).GetField("backstoryId", any), textField = typeof(BiogFile).GetField("questionsStr", any);
        var records = new SortedDictionary<uint, byte[]>();
        int bad = 0;
        foreach (string path in Directory.GetFiles(BiogFile.BIOGSourceFolder, "BIOG*.TXT"))
        {
            string name = Path.GetFileName(path).ToUpperInvariant();
            int classIndex, biographyIndex;
            if (name.Length != 12 || !int.TryParse(name.Substring(4, 2), out classIndex) || !int.TryParse(name.Substring(7, 1), out biographyIndex))
            {
                Debug.LogFormat("VITABAKE biog {0}: not a BIOG##T#.TXT name, skipped", name);
                continue;
            }
            // BiogFile reads only the two indices; the constructor's defaults need ARENA2 (class templates)
            var document = (CharacterDocument)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(CharacterDocument));
            document.classIndex = classIndex;
            document.biographyIndex = biographyIndex;
            var biogFile = new BiogFile(document);

            var biog = new VitaBiogs.Biog
            {
                BackstoryId = (int)idField.GetValue(biogFile),
                Text = (string)textField.GetValue(biogFile),
                QuestionLines = new string[biogFile.Questions.Length][],
                AnswerTexts = new string[biogFile.Questions.Length][],
                AnswerEffects = new string[biogFile.Questions.Length][][],
            };
            for (int q = 0; q < biogFile.Questions.Length; q++)
            {
                BiogFile.Question question = biogFile.Questions[q];
                biog.QuestionLines[q] = (string[])question.Text.Clone();
                biog.AnswerTexts[q] = question.Answers.ConvertAll(a => a.Text).ToArray();
                biog.AnswerEffects[q] = question.Answers.ConvertAll(a => a.Effects.ToArray()).ToArray();
            }
            byte[] record = VitaBiogs.Save(name, biog);
            records[VitaPack.NameKey(name)] = record;
            if (Flatten(VitaBiogs.Parse(record, VitaPack.NameKey(name))) != Flatten(biog))
            {
                Debug.LogFormat("VITABAKE biog {0}: decoded record differs", name);
                bad++;
            }
        }
        long total = VitaPackWriter.Write(output, VitaBiogs.PackMagic, records);
        Debug.LogFormat("VITABAKE done: {0} biogs, {1} differ, {2:F1} MB -> {3}", records.Count, bad, total / 1048576.0, output);
    }

    // Everything a questionnaire holds, as one string to compare
    static string Flatten(VitaBiogs.Biog biog)
    {
        if (biog == null)
            return null;
        var parts = new List<string> { biog.BackstoryId.ToString(), biog.Text };
        for (int q = 0; q < biog.QuestionLines.Length; q++)
        {
            parts.Add("Q" + q + ":" + string.Join("|", biog.QuestionLines[q]));
            for (int a = 0; a < biog.AnswerTexts[q].Length; a++)
                parts.Add("A:" + biog.AnswerTexts[q][a] + "=>" + string.Join("|", biog.AnswerEffects[q][a]));
        }
        return string.Join("\n", parts.ToArray());
    }
}
