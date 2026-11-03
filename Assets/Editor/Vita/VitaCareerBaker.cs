// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Career baker (runtime side in Assets/Scripts/Vita/VitaCareers.cs): every CLASS*.CFG through DFU's own ClassFile and every ENEMY###.CFG
// record of MONSTER.BSA through MonsterFile.LoadMonster, into StreamingAssets/VitaNative/careers.pak. Check per career: the record decodes
// (the Vita's own reader) to a DFCareer that writes the same bytes again and gives the same JsonUtility text. Editor only.
//   vita/tools/bake_careers.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output .pak, both Windows paths)

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;

public static class VitaCareerBaker
{
    public static void Bake()
    {
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        var careers = new SortedDictionary<string, DFCareer>();
        foreach (string path in Directory.GetFiles(arena2, "CLASS*.CFG"))
        {
            var file = new ClassFile();
            if (file.Load(path))
                careers[Path.GetFileName(path).ToUpperInvariant()] = file.Career;
        }
        var monsters = new MonsterFile(Path.Combine(arena2, MonsterFile.Filename), FileUsage.UseMemory, true);
        for (int record = 0; record < monsters.Count; record++)
        {
            string name = monsters.GetRecordName(record).ToUpperInvariant();
            int monster;
            DFCareer career;
            if (name.StartsWith("ENEMY") && int.TryParse(name.Substring(5, 3), out monster) && monsters.LoadMonster(monster, out career))
                careers[name] = career;
            else
                Debug.LogFormat("VITABAKE career: MONSTER.BSA record {0} ({1}) not a monster career, skipped", record, name);
        }

        var records = new SortedDictionary<uint, byte[]>();
        int bad = 0;
        foreach (KeyValuePair<string, DFCareer> career in careers)
        {
            uint key = VitaPack.NameKey(career.Key);
            if (records.ContainsKey(key))
            {
                Debug.LogFormat("VITABAKE failed: key {0:x8} twice ({1})", key, career.Key);
                return;
            }
            byte[] record = VitaCareers.Save(career.Key, career.Value);
            DFCareer decoded = VitaCareers.Parse(record, key);
            if (decoded == null || Convert.ToBase64String(VitaCareers.Save(career.Key, decoded)) != Convert.ToBase64String(record) ||
                JsonUtility.ToJson(decoded) != JsonUtility.ToJson(career.Value))
            {
                Debug.LogFormat("VITABAKE career {0}: decoded career differs", career.Key);
                bad++;
            }
            records[key] = record;
        }
        long total = VitaPackWriter.Write(output, VitaCareers.PackMagic, records);
        Debug.LogFormat("VITABAKE done: {0} careers, {1} differ, {2:F1} MB -> {3}", records.Count, bad, total / 1048576.0, output);
    }
}
