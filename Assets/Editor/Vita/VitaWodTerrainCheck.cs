// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Checks VitaWodTerrain (the CPU port of World of Daggerfall - Terrain's tile kernel) against heights dumped from PC DFU running the mod
// (Vita PC Compare mod, wodref mode: <px>_<py>.bin = int dim + dim*dim floats, [x, y] x-major). Per tile: max/mean difference in metres,
// time. Writes the port's tiles next to them as <px>_<py>.vita.bin. Editor only.
//   VITA_ARENA2=<ARENA2> VITA_WODREF=<dump folder> (Windows paths) vita/unity2017.sh VitaWodTerrainCheck.Run wodcheck

using System;
using System.Diagnostics;
using System.IO;
using DaggerfallWorkshop;
using Debug = UnityEngine.Debug;

public static class VitaWodTerrainCheck
{
    public static void Run()
    {
        DaggerfallUnity.Settings.AssetInjection = false;
        DaggerfallUnity.Instance.ChangeArena2Path(Environment.GetEnvironmentVariable("VITA_ARENA2"));
        string dir = Environment.GetEnvironmentVariable("VITA_WODREF");
        if (!VitaWodTerrain.Load())
        {
            Debug.Log("VITAWOD failed: no wodterrain.bin");
            return;
        }
        new DaggerfallWorkshop.BasicRoads.BasicRoadsTexturing(false, false);   // loads the road/track data the reference had on
        foreach (string file in Directory.GetFiles(dir, "*_*.bin"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Contains("."))
                continue;
            string[] xy = name.Split('_');
            int px = int.Parse(xy[0]), py = int.Parse(xy[1]);
            float[] reference;
            using (BinaryReader r = new BinaryReader(File.OpenRead(file)))
            {
                int dim = r.ReadInt32();
                reference = new float[dim * dim];
                for (int i = 0; i < reference.Length; i++)
                    reference[i] = r.ReadSingle();
            }
            NativeArray<float> data = new NativeArray<float>(129 * 129, Allocator.Persistent);
            Stopwatch watch = Stopwatch.StartNew();
            VitaWodTerrain.Generate(px, py, data);
            watch.Stop();
            double max = 0, sum = 0;
            int worstX = 0, worstY = 0;
            using (BinaryWriter w = new BinaryWriter(File.Create(Path.Combine(dir, name + ".vita.bin"))))
            {
                w.Write(129);
                for (int x = 0; x < 129; x++)
                    for (int y = 0; y < 129; y++)
                    {
                        float mine = data[y + x * 129];
                        w.Write(mine);
                        double d = Math.Abs(mine - reference[x * 129 + y]) * 5000.0;
                        sum += d;
                        if (d > max)
                        {
                            max = d;
                            worstX = x;
                            worstY = y;
                        }
                    }
            }
            Debug.LogFormat("VITAWOD {0},{1}: max diff {2:0.000} m at {3},{4} (ref {5:0.00} m, port {6:0.00} m), mean {7:0.0000} m, {8} ms", px, py, max,
                worstX, worstY, reference[worstX * 129 + worstY] * 5000f, data[worstY + worstX * 129] * 5000f, sum / (129 * 129), watch.ElapsedMilliseconds);
            data.Dispose();
        }
        Debug.Log("VITAWOD done");
    }
}
