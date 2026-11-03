// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Atlas baker. Builds palette sprite atlases with
// DFU's own TextureReader.GetTexture2DAtlas, with the settings MaterialReader.GetMaterialAtlas uses for billboards and the Vita defaults
// (point filter, mips, no normal maps), and writes the records VitaAtlasCache.Save would put on the card into
// StreamingAssets/VitaNative/atlas.pak. Only what the game asks for (all 2048 x 472 archives would be ~260 MB): 1024 for enemies and mobile
// people (DaggerfallMobileUnit, MobilePersonBillboard), 2048 for flats (DaggerfallBillboard, billboard batches). Editor only.
//   vita/tools/bake_atlases.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output .pak, both Windows paths)

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;

public static class VitaAtlasBaker
{
    public static void Bake()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        if (string.IsNullOrEmpty(output))
            output = VitaAtlasCache.PackPath;

        DaggerfallUnity.Settings.AssetInjection = false;
        DaggerfallUnity.Settings.RetroRenderingMode = 0;
        DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
        dfUnity.ChangeArena2Path(arena2);
        if (!dfUnity.IsReady)
        {
            Debug.Log("VITABAKE failed: ARENA2 not valid: " + arena2);
            return;
        }
        dfUnity.MaterialReader.MainFilterMode = FilterMode.Point;
        dfUnity.MaterialReader.GenerateNormals = false;
        var reader = new TextureReader(arena2);

        // key -> record; identical records (bar the key) are stored once
        var keys = new List<uint>();
        var records = new List<byte[]>();
        uint lastKey = 0;
        byte[] last = null;
        VitaAtlasCache.BakeSink = (key, record) => { lastKey = key; last = record; };
        int archives = 0, unpacked = 0;
        HashSet<int> mobiles = MobileArchives(), flats = FlatArchives(dfUnity);
        var wanted = new SortedSet<int>(mobiles);
        wanted.UnionWith(flats);
        foreach (int archive in wanted)
        {
            // Daggerfall's TEXTURE.000-511, or the port's own from 512 up (mod flats, vita/tools/flatpack.py)
            string file = TextureFile.IndexToFileName(archive);
            if (archive >= 512 || !File.Exists(Path.Combine(arena2, file)))     // no-mods alpha: Daggerfall's own archives only
            {
                Debug.LogFormat("VITABAKE atlas {0}: no TEXTURE file", archive);
                continue;
            }
            archives++;
            foreach (int maxSize in new[] { 1024, 2048 })
            {
                if (!(maxSize == 1024 ? mobiles : flats).Contains(archive))
                    continue;
                // Same settings as MaterialReader.GetMaterialAtlas(archive, 0, 4, maxSize, ..., 4, true, 0, false, true)
                GetTextureSettings settings = TextureReader.CreateTextureSettings(archive, 0, 0, 0, 4, true);
                settings.createNormalMap = false;
                settings.autoEmission = true;
                settings.atlasShrinkUVs = 0;
                settings.atlasPadding = 4;
                settings.atlasMaxSize = maxSize;
                settings.copyToOppositeBorder = false;
                settings.vitaPalette = true;
                last = null;
                try
                {
                    GetTextureResults results = reader.GetTexture2DAtlas(settings, dfUnity.MaterialReader.AlphaTextureFormat);
                    if (results.albedoMap)
                        UnityEngine.Object.DestroyImmediate(results.albedoMap);
                    if (results.emissionMap)
                        UnityEngine.Object.DestroyImmediate(results.emissionMap);
                }
                catch (Exception e)
                {
                    Debug.LogFormat("VITABAKE atlas {0} ({1}): {2}", archive, maxSize, e.Message);
                }
                if (last == null)
                {
                    unpacked++;
                    Debug.LogFormat("VITABAKE atlas {0} ({1}): not a palette atlas, left to the runtime path", archive, maxSize);
                    continue;
                }
                keys.Add(lastKey);
                records.Add(last);
                Debug.LogFormat("VITABAKE atlas {0} ({1}): {2}x{3}, {4} bytes", archive, maxSize, BitConverter.ToInt32(last, 8), BitConverter.ToInt32(last, 12), last.Length);
            }
        }
        VitaAtlasCache.BakeSink = null;

        // Header + index, then each distinct record at a 512-byte offset
        var offsets = new int[records.Count];
        var unique = new List<int>();
        long offset = AlignUp(12 + 12 * records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            int same = unique.FindIndex(u => SameExceptKey(records[u], records[i]));
            if (same >= 0)
            {
                offsets[i] = offsets[unique[same]];
                continue;
            }
            unique.Add(i);
            offsets[i] = (int)offset;
            offset = AlignUp(offset + records[i].Length);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        long total;
        using (var writer = new BinaryWriter(new FileStream(output, FileMode.Create)))
        {
            writer.Write(VitaAtlasCache.PackMagic);
            writer.Write(VitaAtlasCache.PackVersion);
            writer.Write(records.Count);
            for (int i = 0; i < records.Count; i++)
            {
                writer.Write(keys[i]);
                writer.Write(offsets[i]);
                writer.Write(records[i].Length);
            }
            foreach (int i in unique)
            {
                writer.Seek(offsets[i], SeekOrigin.Begin);
                writer.Write(records[i]);
            }
            total = writer.BaseStream.Length;
        }
        Debug.LogFormat("VITABAKE done: {0} archives with a TEXTURE file, {1} atlas keys ({2} distinct records), {3} left to the runtime path, {4:F1} MB, {5:F0} s -> {6}",
            archives, records.Count, unique.Count, unpacked, total / 1048576.0, watch.Elapsed.TotalSeconds, output);
    }

    // DaggerfallMobileUnit (EnemyBasics) and MobilePersonBillboard's race/guard tables
    static HashSet<int> MobileArchives()
    {
        var set = new HashSet<int> { 381, 382, 383, 384, 395, 396, 397, 398, 387, 388, 389, 390, 392, 393, 451, 452, 385, 386, 391, 394, 453, 454, 455, 456, 399 };
        foreach (MobileEnemy enemy in EnemyBasics.Enemies)
        {
            set.Add(enemy.MaleTexture);
            set.Add(enemy.FemaleTexture);
        }
        return set;
    }

    // Every flat placed by a block (BLOCKS.BSA and the world-data packs: exterior, interior, people, dungeon), plus what code spawns:
    // corpses, nature billboards (500-511), editor/animal/light/loot flats (199-216), missiles and blood (375-380)
    static HashSet<int> FlatArchives(DaggerfallUnity dfUnity)
    {
        var set = new HashSet<int>();
        for (int a = 199; a <= 216; a++)
            set.Add(a);
        for (int a = 375; a <= 380; a++)
            set.Add(a);
        for (int a = 500; a <= 511; a++)
            set.Add(a);
        foreach (MobileEnemy enemy in EnemyBasics.Enemies)
        {
            int archive, record;
            EnemyBasics.ReverseCorpseTexture(enemy.CorpseTexture, out archive, out record);
            set.Add(archive);
        }
        // Quest people (GameObjectHelper: a faction's flat1/flat2, e.g. Lysandus 473, 171) and quest items (their world texture)
        foreach (FactionFile.FactionData faction in dfUnity.ContentReader.FactionFileReader.FactionDict.Values)
            foreach (int flat in new[] { faction.flat1, faction.flat2 })
                if (flat > 0)
                    set.Add(FactionFile.GetFlatData(flat).archive);
        foreach (DaggerfallConnect.FallExe.ItemTemplate template in DaggerfallUnity.Instance.ItemHelper.ItemTemplates)
            if (template.worldTextureArchive > 0)
                set.Add(template.worldTextureArchive);
        var blocks = new List<DFBlock>();
        var blocksFile = dfUnity.ContentReader.BlockFileReader;
        for (int i = 0; i < blocksFile.Count; i++)
            if (blocksFile.GetBlockType(i) != DFBlock.BlockTypes.Unknown)
                blocks.Add(blocksFile.GetBlock(i));
        foreach (DFBlock block in blocks)
        {
            if (block.Type == DFBlock.BlockTypes.Rmb)
            {
                Add(set, block.RmbBlock.MiscFlatObjectRecords);
                foreach (DFBlock.RmbSubRecord sub in block.RmbBlock.SubRecords ?? new DFBlock.RmbSubRecord[0])
                    foreach (DFBlock.RmbBlockData data in new[] { sub.Exterior, sub.Interior })
                    {
                        Add(set, data.BlockFlatObjectRecords);
                        if (data.BlockPeopleRecords != null)
                            foreach (DFBlock.RmbBlockPeopleRecord person in data.BlockPeopleRecords)
                                set.Add(person.TextureArchive);
                    }
            }
            else if (block.Type == DFBlock.BlockTypes.Rdb && block.RdbBlock.ObjectRootList != null)
                foreach (DFBlock.RdbObjectRoot root in block.RdbBlock.ObjectRootList)
                    if (root.RdbObjects != null)
                        foreach (DFBlock.RdbObject obj in root.RdbObjects)
                            if (obj.Type == DFBlock.RdbResourceTypes.Flat)
                                set.Add(obj.Resources.FlatResource.TextureArchive);
        }
        foreach (int a in new[] { 182, 183, 334, 357 })
            if (set.Contains(a))
                set.Add(a + 10000);
        return set;
    }

    static void Add(HashSet<int> set, DFBlock.RmbBlockFlatObjectRecord[] flats)
    {
        if (flats != null)
            foreach (DFBlock.RmbBlockFlatObjectRecord flat in flats)
                set.Add(flat.TextureArchive);
    }

    // Records differ in the key (bytes 4-7) when two size limits built the same atlas
    static bool SameExceptKey(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
            if ((i < 4 || i >= 8) && a[i] != b[i])
                return false;
        return true;
    }

    static long AlignUp(long position)
    {
        return (position + 511) & ~511L;
    }
}
