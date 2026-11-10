// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// RMB Resource Pack models -> StreamingAssets/VitaNative/rrp_models.bin (same "VDTM" format as det_models.bin, read by VitaDetModels).
// Beautiful Cities/Villages blocks place RRP models (city walls 53210, bridges, docks); the Vita has no RRP, so worldpack.py left them
// out (walls with gaps). The mod's FBX files are imported by this editor (same mesh data DFU gets on the PC) and written with the
// texture keys from each prefab's RuntimeMaterials (vanilla archives, climate applied like vanilla models). Editor only.
//   vita/tools/rrppack.sh   (VITA_RRP_JOBS = jobs file from vita/tools/rrppack.py, VITA_BAKE_OUT = output .bin, Windows paths)
// Jobs: one line per model: id|asset path|scale x,y,z|slot:archive:record;...
// A file with several meshes (stonebridge_001a.fbx: stonebridge_001 + stonebridge_001a) gives the one on the node named like the file:
// prefab 53132's three materials match that node (the other has four); its Unity 2019 mesh fileID can't be mapped to 2018 names.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using DaggerfallWorkshop;

public static class VitaRrpBaker
{
    public static void Bake()
    {
        string jobs = Environment.GetEnvironmentVariable("VITA_RRP_JOBS");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        AssetDatabase.Refresh();
        var models = new List<byte[]>();
        foreach (string line in File.ReadAllLines(jobs))
        {
            string[] f = line.Split('|');
            Mesh mesh = null;
            int meshCount = 0;
            foreach (UnityEngine.Object o in AssetDatabase.LoadAllAssetsAtPath(f[1]))
                if (o is Mesh) { mesh = mesh ?? (Mesh)o; meshCount++; }
            if (meshCount > 1)
            {
                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(f[1]);
                Transform node = model ? model.transform.Find(Path.GetFileNameWithoutExtension(f[1])) : null;
                MeshFilter filter = node ? node.GetComponent<MeshFilter>() : null;
                if (filter && filter.sharedMesh)
                {
                    mesh = filter.sharedMesh;
                    meshCount = 1;
                }
            }
            if (mesh == null || meshCount != 1)
            {
                Debug.LogFormat("VITABAKE rrp skip {0}: {1} meshes in {2}", f[0], meshCount, f[1]);
                continue;
            }
            string[] s = f[2].Split(',');
            Vector3 scale = new Vector3(float.Parse(s[0], CultureInfo.InvariantCulture), float.Parse(s[1], CultureInfo.InvariantCulture),
                float.Parse(s[2], CultureInfo.InvariantCulture));
            var keys = new Dictionary<int, int[]>();
            foreach (string k in f[3].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] p = k.Split(':');
                keys[int.Parse(p[0])] = new[] { int.Parse(p[1]), int.Parse(p[2]) };
            }
            Vector3[] v = mesh.vertices, n = mesh.normals;
            Vector2[] uv = mesh.uv;
            var b = new MemoryStream();
            var w = new BinaryWriter(b);
            Vector3 min = Vector3.one * float.MaxValue, max = Vector3.one * float.MinValue;
            for (int i = 0; i < v.Length; i++)
            {
                v[i] = Vector3.Scale(v[i], scale);
                min = Vector3.Min(min, v[i]);
                max = Vector3.Max(max, v[i]);
            }
            Vector3 size = (max - min) / MeshReader.GlobalScale;
            w.Write(uint.Parse(f[0])); w.Write(size.x); w.Write(size.y); w.Write(size.z); w.Write(v.Length);
            foreach (Vector3 x in v) { w.Write(x.x); w.Write(x.y); w.Write(x.z); }
            for (int i = 0; i < v.Length; i++) { Vector3 x = n.Length == v.Length ? n[i] : Vector3.up; w.Write(x.x); w.Write(x.y); w.Write(x.z); }
            for (int i = 0; i < v.Length; i++) { Vector2 x = uv.Length == v.Length ? uv[i] : Vector2.zero; w.Write(x.x); w.Write(x.y); }
            // Submeshes past the prefab's materials are not drawn on the PC (fewer renderer materials than submeshes): left out
            var subs = new List<int>();
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
                if (keys.ContainsKey(sm))
                    subs.Add(sm);
            int missing = mesh.subMeshCount - subs.Count;
            w.Write(subs.Count);
            foreach (int sm in subs)
            {
                int[] tris = mesh.GetTriangles(sm);
                w.Write(keys[sm][0]); w.Write(keys[sm][1]); w.Write(tris.Length);
                foreach (int t in tris) w.Write(t);
            }
            Debug.LogFormat("VITABAKE rrp {0} {1} ({6}): {2} vertices, {3} submeshes{4}, size {5}", f[0], f[1], v.Length, mesh.subMeshCount,
                missing > 0 ? " (" + missing + " without a material, left out)" : "", size, mesh.name);
            models.Add(b.ToArray());
        }
        using (var w = new BinaryWriter(File.Create(output)))
        {
            w.Write(Encoding.ASCII.GetBytes("VDTM")); w.Write(1); w.Write(models.Count);
            foreach (byte[] m in models) w.Write(m);
        }
        Debug.LogFormat("VITABAKE rrp done: {0} models -> {1}", models.Count, output);
    }
}
