// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Model baker. Runs DFU's own MeshReader.GetModelData
// for every model in ARCH3D.BSA and writes the ModelData into StreamingAssets/VitaNative/models.pak (VitaPack version 1, key = model
// GetModelData returns those first anyway. Editor only.
//   vita/tools/bake_models.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output .pak, both Windows paths)

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;

public static class VitaModelBaker
{
    public static void Bake()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        if (string.IsNullOrEmpty(output))
            output = VitaModelPack.PackPath;

        DaggerfallUnity.Settings.AssetInjection = false;
        DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
        dfUnity.ChangeArena2Path(arena2);
        if (!dfUnity.IsReady)
        {
            Debug.Log("VITABAKE failed: ARENA2 not valid: " + arena2);
            return;
        }
        VitaModelPack.Disabled = true;   // DFU's own read, never an older pack

        var arch3d = new Arch3dFile(Path.Combine(arena2, Arch3dFile.Filename), DaggerfallConnect.FileUsage.UseMemory, true);
        var ids = new List<uint>();
        var records = new List<byte[]>();
        var seen = new HashSet<uint>();
        int replaced = 0, failed = 0;
        long vertices = 0;
        for (int r = 0; r < arch3d.Count; r++)
        {
            uint id = arch3d.GetRecordId(r);
            if (!seen.Add(id))
                continue;
            ModelData modelData;
#if VITA_MODS
            if (VitaDetModels.TryGetModelData(id, out modelData))
            {
                replaced++;
                continue;
            }
#endif
            if (!dfUnity.MeshReader.GetModelData(id, out modelData))
            {
                failed++;   // no vertices: GetModelData fails for it on the Vita too
                continue;
            }
            ids.Add(id);
            records.Add(VitaModelPack.Serialize(ref modelData));
            vertices += modelData.Vertices.Length;
            if (ids.Count % 500 == 0)
                dfUnity.MeshReader.ClearCache();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output));
        long total;
        using (var writer = new BinaryWriter(new FileStream(output, FileMode.Create)))
        {
            writer.Write(VitaModelPack.Magic);
            writer.Write(VitaPack.Version);
            writer.Write(ids.Count);
            long offset = AlignUp(12 + 12 * ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                writer.Write(ids[i]);
                writer.Write((int)offset);
                writer.Write(records[i].Length);
                offset = AlignUp(offset + records[i].Length);
            }
            for (int i = 0; i < ids.Count; i++)
            {
                writer.Seek((int)AlignUp(writer.BaseStream.Position), SeekOrigin.Begin);
                writer.Write(records[i]);
            }
            total = writer.BaseStream.Length;
        }
        Debug.LogFormat("VITABAKE done: {0} models ({1} left out as replaced, {2} without vertices), {3} vertices, {4:F1} MB, {5:F0} s -> {6}",
            ids.Count, replaced, failed, vertices, total / 1048576.0, watch.Elapsed.TotalSeconds, output);
    }

    static long AlignUp(long position)
    {
        return (position + 511) & ~511L;
    }
}
