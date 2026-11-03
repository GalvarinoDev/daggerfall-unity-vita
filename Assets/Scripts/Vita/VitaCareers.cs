// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked class and monster careers. CLASS00-17.CFG and MONSTER.BSA's
// ENEMY###.CFG records were read and structured into a DFCareer (bit fields decoded into skills, tolerances, proficiencies, flags) on the
// Vita, MONSTER.BSA reloaded for every enemy setup (DaggerfallEntity.GetMonsterCareerTemplate). Assets/Editor/Vita/VitaCareerBaker.cs
// runs DFU's own ClassFile/MonsterFile on the PC into StreamingAssets/VitaNative/careers.pak ("VCAR", VitaPack layout, LZ4 by
// vita/tools/lz4pack.py): per CFG name (key = VitaPack.NameKey, e.g. "CLASS00.CFG", "ENEMY000.CFG") every field of the finished DFCareer,
// written by VitaFields. Each Read makes a new DFCareer (callers may change theirs). Miss: "VITANATIVE miss career <name>".
#if UNITY_PSP2
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using DaggerfallConnect;

public static class VitaCareers
{
    public const int PackMagic = 0x52414356;    // "VCAR"
    const int Magic = 0x52434356;               // "VCCR", each record

    public static Action<uint, byte[]> BakeSink;     // VitaCareerBaker

    static VitaPack pack;

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "careers.pak"); }
    }

    /// <summary>A new DFCareer for a CFG name, or null (no pack, or not baked: logged).</summary>
    public static DFCareer Read(string cfgName)
    {
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        if (pack.Count == 0)
            return null;
        uint key = VitaPack.NameKey(cfgName);
        IntPtr memory = IntPtr.Zero, data;
        int length;
        try
        {
            DFCareer career = null;
            if (pack.Read(key, out memory, out data, out length))
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, length);
                career = Parse(bytes, key);
            }
            if (career == null)
                Debug.LogFormat("VITANATIVE miss career {0}", cfgName);
            return career;
        }
        finally
        {
            if (memory != IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>The baked CLASS##.CFG files as ARENA2 paths in name order (what Directory.GetFiles(ARENA2, "CLASS*.CFG") gives on the PC), or
    /// null if there is no pack: an ARENA2 listing costs seconds on the Vita.</summary>
    public static string[] ClassFiles(string arena2Path)
    {
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        if (pack.Count == 0)
            return null;
        var files = new List<string>();
        for (int i = 0; i < 100; i++)
        {
            string name = string.Format("CLASS{0:00}.CFG", i);
            if (pack.Contains(VitaPack.NameKey(name)))
                files.Add(Path.Combine(arena2Path, name));
        }
        return files.ToArray();
    }

    /// <summary>A record as Read decodes it (also VitaCareerBaker's check): null if damaged.</summary>
    public static DFCareer Parse(byte[] bytes, uint key)
    {
        try
        {
            using (var reader = new BinaryReader(new MemoryStream(bytes)))
                return reader.ReadInt32() == Magic && reader.ReadUInt32() == key ? (DFCareer)VitaFields.Read(reader, typeof(DFCareer)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Bake (editor): one career's record; returns its bytes.</summary>
    public static byte[] Save(string cfgName, DFCareer career)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(VitaPack.NameKey(cfgName));
            VitaFields.Write(writer, typeof(DFCareer), career);
            writer.Flush();
            byte[] record = stream.ToArray();
            if (BakeSink != null)
                BakeSink(VitaPack.NameKey(cfgName), record);
            return record;
        }
    }
}
#endif
