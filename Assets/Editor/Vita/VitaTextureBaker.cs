// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// World texture baker. Runs DFU's own
// TextureReader.GetTexture2D for every record and frame of TEXTURE.000-511 and 9000-9005 with MaterialReader.GetMaterial's settings and the Vita
// defaults (point filter, mips, no normal maps, no sharpening); VitaWorldTextures.Save hands over what the palette path uploaded, and this
// writes StreamingAssets/VitaNative/textures.pak (VitaPack layout: magic, version, count, key/offset/length, records at 512-byte offsets,
// identical records stored once). Editor only.
//   vita/tools/bake_textures.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output .pak, both Windows paths)
// VITA_BAKE_ONLY = a folder of VTEX archives (TEXTURE.<n>, n < 512): bake just those, read from there (a texture mod's own pack,

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;

public static class VitaTextureBaker
{
    public static void Bake()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        if (string.IsNullOrEmpty(output))
            output = VitaWorldTextures.PackPath;

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
        dfUnity.MaterialReader.Sharpen = false;
        var reader = new TextureReader(arena2);
        reader.MipMaps = true;

        var records = new Dictionary<uint, byte[]>();
        VitaWorldTextures.BakeSink = (key, record) => records[key] = record;
        int archives = 0, textures = 0, left = 0, tilesets = 0;
        long pixels = 0;
        // atlas.pak's; archives from 32768 up would reach PaletteFlag in the key.
        var archiveList = new List<int>();
        string only = Environment.GetEnvironmentVariable("VITA_BAKE_ONLY");
        if (string.IsNullOrEmpty(only))
            only = null;
        for (int archive = 0; archive < 512; archive++)
            if (only == null || File.Exists(Path.Combine(only, TextureFile.IndexToFileName(archive))))
                archiveList.Add(archive);
        foreach (int archive in archiveList)
        {
            string path = only != null ? Path.Combine(only, TextureFile.IndexToFileName(archive)) :
                archive < 512 ? Path.Combine(arena2, TextureFile.IndexToFileName(archive)) :
                null;   // no-mods alpha: only archives below 512 are listed
            if (!File.Exists(path))
            {
                if (archive >= 512)
                    Debug.Log("VITABAKE texture archive missing: " + path);
                continue;
            }
            var textureFile = new TextureFile(path, DaggerfallConnect.FileUsage.UseMemory, true);
            archives++;
            for (int record = 0; record < textureFile.RecordCount; record++)
                for (int frame = 0; frame < textureFile.GetFrameCount(record); frame++)
                {
                    // MaterialReader.GetMaterial(archive, record, frame) with its defaults (alpha index -1, no border)
                    GetTextureSettings settings = TextureReader.CreateTextureSettings(archive, record, frame, -1, 0, false);
                    settings.autoEmissionForWindows = true;
                    if (reader.IsEmissive(archive, record))
                    {
                        settings.createEmissionMap = true;
                        settings.emissionIndex = -1;
                    }
                    settings.vitaPalette = true;
                    uint key = VitaWorldTextures.Key(archive, record, frame);
                    try
                    {
                        GetTextureResults results = reader.GetTexture2D(settings, dfUnity.MaterialReader.AlphaTextureFormat);
                        if (results.albedoMap)
                        {
                            pixels += results.albedoMap.width * results.albedoMap.height;
                            UnityEngine.Object.DestroyImmediate(results.albedoMap);
                        }
                        if (results.emissionMap)
                            UnityEngine.Object.DestroyImmediate(results.emissionMap);
                    }
                    catch (Exception e)
                    {
                        Debug.LogFormat("VITABAKE texture {0} {1} {2}: {3}", archive, record, frame, e.Message);
                    }
                    if (records.ContainsKey(key))
                        textures++;
                    else
                    {
                        left++;
                        Debug.LogFormat("VITABAKE texture {0} {1} {2}: not a palette texture, left to the runtime path", archive, record, frame);
                    }
                }
            // Ground archives (56 tiles): the terrain tileset atlas, as MaterialReader.GetTerrainTilesetMaterial asks for it
            if (textureFile.RecordCount == 56)
            {
                GetTextureResults tileset = reader.GetTerrainTilesetTexture(archive);
                if (tileset.albedoMap)
                    UnityEngine.Object.DestroyImmediate(tileset.albedoMap);
                if (records.ContainsKey(VitaWorldTextures.TilesetFlag | (uint)archive))
                    tilesets++;
                else
                    Debug.LogFormat("VITABAKE tileset {0}: not baked", archive);
            }
        }
        VitaWorldTextures.BakeSink = null;

        // Header + index, then each distinct record at a 512-byte offset
        var keys = new List<uint>(records.Keys);
        keys.Sort();
        var offsets = new Dictionary<uint, int>();
        var byContent = new Dictionary<string, int>();
        var unique = new List<uint>();
        long offset = AlignUp(12 + 12 * keys.Count);
        foreach (uint key in keys)
        {
            string hash = Convert.ToBase64String(System.Security.Cryptography.SHA1.Create().ComputeHash(records[key]));
            int same;
            if (byContent.TryGetValue(hash, out same))
            {
                offsets[key] = same;
                continue;
            }
            byContent[hash] = offsets[key] = (int)offset;
            unique.Add(key);
            offset = AlignUp(offset + records[key].Length);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        long total;
        using (var writer = new BinaryWriter(new FileStream(output, FileMode.Create)))
        {
            writer.Write(VitaWorldTextures.PackMagic);
            writer.Write(VitaPack.Version);
            writer.Write(keys.Count);
            foreach (uint key in keys)
            {
                writer.Write(key);
                writer.Write(offsets[key]);
                writer.Write(records[key].Length);
            }
            foreach (uint key in unique)
            {
                writer.Seek(offsets[key], SeekOrigin.Begin);
                writer.Write(records[key]);
            }
            total = writer.BaseStream.Length;
        }
        Debug.LogFormat("VITABAKE done: {0} archives, {1} textures + {9} tilesets baked, {2} left to the runtime path, {3} keys ({4} distinct records), {5:F1} Mpixels level 0, {6:F1} MB, {7:F0} s -> {8}",
            archives, textures, left, keys.Count, unique.Count, pixels / 1e6, total / 1048576.0, watch.Elapsed.TotalSeconds, output, tilesets);
    }

    static long AlignUp(long position)
    {
        return (position + 511) & ~511L;
    }
}
