// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Block baker. Builds every RMB block of BLOCKS.BSA
// (CombinedModels mesh + texture keys, static doors and buildings, standalone models) and writes StreamingAssets/VitaNative/blocks.pak.
// Blocks are collected as the game gets them for every combination of the built-in mods (WorldDataReplacement with each set of packs:
// a block from the first pack that has it, building replacements of the others laid over it), so layered blocks (Cities + Finding My
// "import this model here" entries; no other replacement models. Editor only.
//   vita/tools/bake_blocks.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output .pak, both Windows paths)

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;

public static class VitaBlockBaker
{
    public static void Bake()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        if (string.IsNullOrEmpty(output))
            output = VitaBlockBake.PackPath;

        DaggerfallUnity.Settings.AssetInjection = false;
        DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
        dfUnity.Option_CombineRMB = true;
        dfUnity.ChangeArena2Path(arena2);
        if (!dfUnity.IsReady)
        {
            Debug.Log("VITABAKE failed: ARENA2 not valid: " + arena2);
            return;
        }

        dfUnity.Option_CombineRDB = true;
        GameObject player = StandInPlayer();
        int vanilla, layered;
        var rdbBlocks = new List<DFBlock>();
        List<DFBlock> blocks = Collect(arena2, out vanilla, out layered, rdbBlocks);
        SetPlaceholderImport();

        var names = new List<string>();
        var hashes = new List<uint>();
        var records = new List<byte[]>();
        var done = new HashSet<string>();
        long vertices = 0;
        int skipped = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            DFBlock block = blocks[i];
            uint hash = VitaBlockBake.Hash(ref block);
            if (!done.Add(VitaBlockBake.Key(block.Name, hash)))
                continue;
            int vertexCount;
            byte[] record = BakeBlock(ref block, out vertexCount);
            if (record == null)
            {
                skipped++;
                continue;
            }
            names.Add(block.Name);
            hashes.Add(hash);
            records.Add(record);
            vertices += vertexCount;
        }
        // Dungeon blocks: the combined models only, for both exit door choices (GameObjectHelper: start blocks keep their exits)
        int rdbRecords = 0;
        foreach (DFBlock b in rdbBlocks)
            foreach (bool allowExitDoors in new[] { true, false })
            {
                DFBlock block = b;
                uint hash = VitaBlockBake.RdbHash(ref block, allowExitDoors);
                if (!done.Add(VitaBlockBake.Key(block.Name, hash)))
                    continue;
                int vertexCount;
                byte[] record = BakeRdb(ref block, allowExitDoors, out vertexCount);
                if (record == null)
                {
                    skipped++;
                    continue;
                }
                names.Add(block.Name);
                hashes.Add(hash);
                records.Add(record);
                vertices += vertexCount;
                rdbRecords++;
            }

        // Header + index, then each record at a 512-byte offset
        int indexSize = 12;
        foreach (string name in names)
            indexSize += 4 + Encoding.ASCII.GetByteCount(name) + 16;
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var packed = new List<byte[]>();
        foreach (byte[] record in records)
            packed.Add(Deflate(record));
        long total;
        using (var writer = new BinaryWriter(new FileStream(output, FileMode.Create)))
        {
            writer.Write(Encoding.ASCII.GetBytes(VitaBlockBake.Magic));
            writer.Write(VitaBlockBake.Version);
            writer.Write(names.Count);
            long offset = AlignUp(indexSize);
            for (int i = 0; i < names.Count; i++)
            {
                writer.Write(Encoding.ASCII.GetByteCount(names[i]));
                writer.Write(Encoding.ASCII.GetBytes(names[i]));
                writer.Write(hashes[i]);
                writer.Write((int)offset);
                writer.Write(packed[i].Length);
                writer.Write(records[i].Length);
                offset = AlignUp(offset + packed[i].Length);
            }
            for (int i = 0; i < records.Count; i++)
            {
                writer.Seek((int)AlignUp(writer.BaseStream.Position), SeekOrigin.Begin);
                writer.Write(packed[i]);
            }
            total = writer.BaseStream.Length;
        }
        Debug.LogFormat("VITABAKE done: {0} records ({1} BLOCKS.BSA, {2} BLOCKS.BSA with mods laid over, {3} pack blocks, {9} dungeon from {10} RDB blocks, {4} skipped), {5} vertices, {6:F1} MB, {7:F0} s -> {8}",
            records.Count, vanilla, layered, blocks.Count - vanilla - layered, skipped, vertices, total / 1048576.0, watch.Elapsed.TotalSeconds, output,
            rdbRecords, rdbBlocks.Count);
        DaggerfallWorkshop.Utility.AssetInjection.MeshReplacement.VitaBakeImport = null;
        UnityEngine.Object.DestroyImmediate(player);
    }

    // DaggerfallMesh.SetDungeonTextures reads the climate from the player's PlayerGPS, which the editor has none of (the combined models
    // don't depend on it: the game swaps the dungeon textures on its own climate after building)
    static GameObject StandInPlayer()
    {
        var player = new GameObject("Player");
        player.tag = "Player";
        player.AddComponent<PlayerGPS>();
        return player;
    }

    // Every RMB block as the game can get it: BLOCKS.BSA under each of the 32 mod combinations, then the packs' own new blocks.
    // The RDB blocks go to rdbBlocks the same way (one per distinct content).
    static List<DFBlock> Collect(string arena2, out int vanilla, out int layered, List<DFBlock> rdbBlocks)
    {
        var rdbCollected = new HashSet<string>();
        var blocks = new List<DFBlock>();
        var collected = new HashSet<string>();
        vanilla = 0;
        SettingsManager s = DaggerfallUnity.Settings;
        const int combos = 1;       // no-mods alpha: Daggerfall's own blocks only
        for (int combo = 0; combo < combos; combo++)
        {
            // BLOCKS.BSA as BlocksFile.GetBlock gives it with these mods on (a new reader each time: it caches built blocks)
            s.AssetInjection = combo != 0;
            var blocksFile = new BlocksFile(Path.Combine(arena2, BlocksFile.Filename), FileUsage.UseMemory, true);
            for (int i = 0; i < blocksFile.Count; i++)
                if (blocksFile.GetBlockType(i) == DFBlock.BlockTypes.Rmb)
                {
                    DFBlock block = blocksFile.GetBlock(i);
                    if (collected.Add(VitaBlockBake.Key(block.Name, VitaBlockBake.Hash(ref block))))
                        blocks.Add(block);
                    if (combo == 0)
                        vanilla++;
                }
                else if (blocksFile.GetBlockType(i) == DFBlock.BlockTypes.Rdb)
                {
                    DFBlock block = blocksFile.GetBlock(i);
                    if (rdbCollected.Add(VitaBlockBake.Key(block.Name, VitaBlockBake.RdbHash(ref block, true))))
                        rdbBlocks.Add(block);
                }
        }
        s.AssetInjection = false;
        layered = blocks.Count - vanilla;

        return blocks;
    }

    static void SetPlaceholderImport()
    {
        DaggerfallWorkshop.Utility.AssetInjection.MeshReplacement.VitaBakeImport = (id, parent, matrix) =>
        {
            var placeholder = new GameObject(GameObjectHelper.GetGoModelName(id) + " [Replacement]");
            placeholder.transform.parent = parent;
            placeholder.transform.position = matrix.GetColumn(3);
            placeholder.transform.rotation = matrix.rotation;
            placeholder.transform.localScale = matrix.lossyScale;
            return placeholder;
        };
    }

    // Builds every collected block both ways (DFU's path and VitaBlockBake.TryBuild from the pack) and compares the results.
    //   vita/unity2017.sh VitaBlockBaker.Verify verifyblocks   (after bake_blocks.sh)
    public static void Verify()
    {
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        DaggerfallUnity.Settings.AssetInjection = false;
        DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
        dfUnity.Option_CombineRMB = true;
        dfUnity.Option_CombineRDB = true;
        dfUnity.ChangeArena2Path(arena2);
        GameObject player = StandInPlayer();
        int vanilla, layered;
        var rdbBlocks = new List<DFBlock>();
        List<DFBlock> blocks = Collect(arena2, out vanilla, out layered, rdbBlocks);
        SetPlaceholderImport();
        int same = 0, differ = 0;
        var done = new HashSet<string>();
        foreach (DFBlock b in blocks)
        {
            DFBlock block = b;
            if (!done.Add(VitaBlockBake.Key(block.Name, VitaBlockBake.Hash(ref block))))
                continue;
            GameObject x = RMBLayout.CreateBaseGameObject(ref block, 3, 5);
            GameObject y = new GameObject(x.name);
            string diff = VitaBlockBake.TryBuild(ref block, 3, 5, y) ? Compare(x, y) : "not built";
            if (diff == null)
                same++;
            else if (differ++ < 20)
                Debug.Log("VITABAKE verify " + block.Name + ": " + diff);
            foreach (GameObject go in new[] { x, y })
            {
                Transform combined = go.transform.Find("Models/CombinedModels");
                if (combined != null)
                    UnityEngine.Object.DestroyImmediate(combined.GetComponent<MeshFilter>().sharedMesh);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
        // Dungeon blocks: DFU's combined models vs the record's
        int rdbSame = 0;
        foreach (DFBlock b in rdbBlocks)
            foreach (bool allowExitDoors in new[] { true, false })
            {
                DFBlock block = b;
                if (!done.Add(VitaBlockBake.Key(block.Name, VitaBlockBake.RdbHash(ref block, allowExitDoors))))
                    continue;
                GameObject x = RDBLayout.CreateBaseGameObject(ref block, new Dictionary<int, RDBLayout.ActionLink>(), null, allowExitDoors, null, false);
                Transform cx = x.transform.Find("Models/CombinedModels");
                var combiner = new ModelCombiner();
                string diff;
                GameObject y = null;
                if (!VitaBlockBake.TrySealRdb(ref block, allowExitDoors, combiner))
                    diff = "not baked";
                else if (combiner.VertexCount == 0)
                    diff = cx == null ? null : "no combined models in the record";
                else if (cx == null)
                    diff = "combined models in the record only";
                else
                {
                    y = GameObjectHelper.CreateCombinedMeshGameObject(combiner, "CombinedModels", null, false);
                    y.GetComponent<DaggerfallMesh>().SetDungeonTextures(DungeonTextureTables.DefaultTextureTable);
                    diff = CompareCombined(cx, y.transform);
                }
                if (diff == null)
                    rdbSame++;
                else if (differ++ < 40)
                    Debug.Log("VITABAKE verify " + block.Name + (allowExitDoors ? "" : " (no exits)") + ": " + diff);
                if (cx != null)
                    UnityEngine.Object.DestroyImmediate(cx.GetComponent<MeshFilter>().sharedMesh);
                if (y != null)
                {
                    UnityEngine.Object.DestroyImmediate(y.GetComponent<MeshFilter>().sharedMesh);
                    UnityEngine.Object.DestroyImmediate(y);
                }
                UnityEngine.Object.DestroyImmediate(x);
            }
        DaggerfallWorkshop.Utility.AssetInjection.MeshReplacement.VitaBakeImport = null;
        Debug.LogFormat("VITABAKE verify: {0} same, {1} dungeon same, {2} differ", same, rdbSame, differ);
        UnityEngine.Object.DestroyImmediate(player);
    }

    static string Compare(GameObject a, GameObject b)
    {
        Transform ma = a.transform.Find("Models"), mb = b.transform.Find("Models");
        if (ma.childCount != mb.childCount)
            return "children " + ma.childCount + " vs " + mb.childCount;
        for (int i = 0; i < ma.childCount; i++)
        {
            Transform ca = ma.GetChild(i), cb = mb.GetChild(i);
            if (ca.name != cb.name || ca.position != cb.position || ca.rotation != cb.rotation || ca.localScale != cb.localScale)
                return "child " + i + " " + ca.name + " vs " + cb.name;
            MeshFilter fa = ca.GetComponent<MeshFilter>(), fb = cb.GetComponent<MeshFilter>();
            if (ca.name == "CombinedModels")
            {
                string diff = CompareCombined(ca, cb);
                if (diff != null)
                    return diff;
            }
            else if ((fa == null) != (fb == null))
                return "mesh filter " + ca.name;
            else if (fa != null && (!Same(fa.sharedMesh.vertices, fb.sharedMesh.vertices) || !Same(fa.sharedMesh.uv, fb.sharedMesh.uv)))
                return "standalone mesh " + ca.name;
        }
        DaggerfallStaticDoors da = a.GetComponent<DaggerfallStaticDoors>(), db = b.GetComponent<DaggerfallStaticDoors>();
        if ((da == null) != (db == null) || da != null && JsonUtility.ToJson(da) != JsonUtility.ToJson(db))
            return "doors";
        DaggerfallStaticBuildings ba = a.GetComponent<DaggerfallStaticBuildings>(), bb = b.GetComponent<DaggerfallStaticBuildings>();
        if ((ba == null) != (bb == null) || ba != null && JsonUtility.ToJson(ba) != JsonUtility.ToJson(bb))
            return "buildings";
        return null;
    }

    // Every triangle corner the same (the bake welds vertices, so compare through the indices), and the same textures
    static string CompareCombined(Transform ca, Transform cb)
    {
        Mesh x = ca.GetComponent<MeshFilter>().sharedMesh, y = cb.GetComponent<MeshFilter>().sharedMesh;
        DaggerfallMesh da = ca.GetComponent<DaggerfallMesh>(), db = cb.GetComponent<DaggerfallMesh>();
        if (x.subMeshCount != y.subMeshCount || !Same(da.GetDefaultTextures(), db.GetDefaultTextures()) ||
            !Same(ca.GetComponent<MeshRenderer>().sharedMaterials, cb.GetComponent<MeshRenderer>().sharedMaterials))
            return "combined mesh";
        Vector3[] xv = x.vertices, yv = y.vertices, xn = x.normals, yn = y.normals;
        Vector2[] xu = x.uv, yu = y.uv;
        for (int s = 0; s < x.subMeshCount; s++)
        {
            int[] xt = x.GetTriangles(s), yt = y.GetTriangles(s);
            if (xt.Length != yt.Length)
                return "submesh " + s;
            for (int k = 0; k < xt.Length; k++)
                if (!xv[xt[k]].Equals(yv[yt[k]]) || !xn[xt[k]].Equals(yn[yt[k]]) || !xu[xt[k]].Equals(yu[yt[k]]))
                    return "submesh " + s + " corner " + k;
        }
        return null;
    }

    static bool Same<T>(T[] x, T[] y)
    {
        if (x.Length != y.Length)
            return false;
        for (int i = 0; i < x.Length; i++)
            if (!EqualityComparer<T>.Default.Equals(x[i], y[i]))
                return false;
        return true;
    }

    static byte[] Deflate(byte[] data)
    {
        var stream = new MemoryStream();
        using (var deflate = new DeflateStream(stream, CompressionMode.Compress))
            deflate.Write(data, 0, data.Length);
        return stream.ToArray();
    }

    static long AlignUp(long n)
    {
        return (n + VitaBlockBake.Align - 1) / VitaBlockBake.Align * VitaBlockBake.Align;
    }

    // One block, in VitaBlockBake.TryBuild's order; null if it can't be baked
    static byte[] BakeBlock(ref DFBlock block, out int vertexCount)
    {
        vertexCount = 0;
        GameObject go = RMBLayout.CreateBaseGameObject(ref block, 0, 0);
        if (go == null)
        {
            Debug.Log("VITABAKE skip " + block.Name + ": not built");
            return null;
        }
        try
        {
            Transform models = go.transform.Find("Models");
            Mesh mesh = null;
            int[] textureKeys = null;
            var standalones = new List<Transform>();
            foreach (Transform child in models)
            {
                if (child.name == "CombinedModels")
                {
                    mesh = child.GetComponent<MeshFilter>().sharedMesh;
                    textureKeys = child.GetComponent<DaggerfallMesh>().GetDefaultTextures();
                }
                else if (child.name.StartsWith("DaggerfallMesh [ID="))
                    standalones.Add(child);
                else
                {
                    Debug.Log("VITABAKE skip " + block.Name + ": unexpected " + child.name);
                    return null;
                }
            }

            var stream = new MemoryStream();
            var w = new BinaryWriter(stream);
            if (!WriteCombined(w, mesh, textureKeys, block.Name, out vertexCount))
                return null;

            DaggerfallStaticDoors doors = go.GetComponent<DaggerfallStaticDoors>();
            StaticDoor[] doorArray = doors != null && doors.Doors != null ? doors.Doors : new StaticDoor[0];
            w.Write(doorArray.Length);
            foreach (StaticDoor d in doorArray)
            {
                Write(w, d.buildingMatrix);
                w.Write((int)d.doorType);
                w.Write(d.recordIndex);
                w.Write(d.doorIndex);
                Write(w, d.centre);
                Write(w, d.size);
                Write(w, d.normal);
            }

            DaggerfallStaticBuildings buildings = go.GetComponent<DaggerfallStaticBuildings>();
            StaticBuilding[] buildingArray = buildings != null && buildings.Buildings != null ? buildings.Buildings : new StaticBuilding[0];
            w.Write(buildingArray.Length);
            foreach (StaticBuilding b in buildingArray)
            {
                Write(w, b.modelMatrix);
                w.Write(b.recordIndex);
                Write(w, b.centre);
                Write(w, b.size);
            }

            w.Write(standalones.Count);
            foreach (Transform t in standalones)
            {
                string id = t.name.Substring("DaggerfallMesh [ID=".Length);
                int modelId = int.Parse(id.Substring(0, id.IndexOf(']')));
                w.Write(t.name.EndsWith("[Replacement]") ? modelId | VitaBlockBake.ReplacementFlag : modelId);
                Write(w, t.position);
                Quaternion q = t.rotation;
                w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
                Write(w, t.localScale);
            }
            return stream.ToArray();
        }
        finally
        {
            // The combined mesh is made per block; standalone meshes belong to MeshReader's cache
            Transform combined = go.transform.Find("Models/CombinedModels");
            if (combined != null)
                UnityEngine.Object.DestroyImmediate(combined.GetComponent<MeshFilter>().sharedMesh);
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    // Combined model as ModelCombiner.Apply() made it (one vertex per index), with identical vertices welded inside each submesh: the same
    // triangles and attributes, a third or less of the vertices (VitaAtlas keeps per-submesh UV rects, so no welding across submeshes).
    // An empty section without a mesh. False if the mesh isn't Apply()'s layout.
    static bool WriteCombined(BinaryWriter w, Mesh mesh, int[] textureKeys, string name, out int vertexCount)
    {
        vertexCount = 0;
        if (mesh == null)
        {
            w.Write(0);
            w.Write(0);
            w.Write(0);
            return true;
        }
        Vector3[] v = mesh.vertices, n = mesh.normals;
        Vector2[] uv = mesh.uv;
        var wv = new List<Vector3>();
        var wn = new List<Vector3>();
        var wuv = new List<Vector2>();
        var indices = new List<int>();
        var subMeshes = new List<int[]>();
        int next = 0;
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            int[] tris = mesh.GetTriangles(s);
            var welded = new Dictionary<WeldKey, int>();
            int start = indices.Count;
            for (int t = 0; t < tris.Length; t++)
            {
                if (tris[t] != next++)
                {
                    Debug.Log("VITABAKE skip " + name + ": indices not sequential");
                    return false;
                }
                var key = new WeldKey(v[tris[t]], n[tris[t]], uv[tris[t]]);
                int index;
                if (!welded.TryGetValue(key, out index))
                {
                    index = wv.Count;
                    welded.Add(key, index);
                    wv.Add(key.p);
                    wn.Add(key.n);
                    wuv.Add(key.uv);
                }
                indices.Add(index);
            }
            int archive, record, frame;
            MaterialReader.ReverseTextureKey(textureKeys[s], out archive, out record, out frame);
            subMeshes.Add(new[] { archive, record, start, tris.Length / 3 });
        }
        if (next != v.Length)
        {
            Debug.Log("VITABAKE skip " + name + ": index count != vertex count");
            return false;
        }
        vertexCount = wv.Count;
        w.Write(wv.Count);
        w.Write(subMeshes.Count);
        foreach (int[] sm in subMeshes)
            foreach (int x in sm)
                w.Write(x);
        foreach (Vector3 p in wv) Write(w, p);
        foreach (Vector3 p in wn) Write(w, p);
        foreach (Vector2 p in wuv) { w.Write(p.x); w.Write(p.y); }
        w.Write(indices.Count);
        foreach (int i in indices) w.Write(i);
        return true;
    }

    // One dungeon block's combined models, in VitaBlockBake.TrySealRdb's order (the door/building/standalone sections stay empty)
    static byte[] BakeRdb(ref DFBlock block, bool allowExitDoors, out int vertexCount)
    {
        vertexCount = 0;
        GameObject go = RDBLayout.CreateBaseGameObject(ref block, new Dictionary<int, RDBLayout.ActionLink>(), null, allowExitDoors, null, false);
        if (go == null)
        {
            Debug.Log("VITABAKE skip " + block.Name + ": not built");
            return null;
        }
        Transform combined = go.transform.Find("Models/CombinedModels");
        try
        {
            var stream = new MemoryStream();
            var w = new BinaryWriter(stream);
            if (!WriteCombined(w, combined ? combined.GetComponent<MeshFilter>().sharedMesh : null,
                combined ? combined.GetComponent<DaggerfallMesh>().GetDefaultTextures() : null, block.Name, out vertexCount))
                return null;
            w.Write(0);
            w.Write(0);
            w.Write(0);
            return stream.ToArray();
        }
        finally
        {
            if (combined != null)
                UnityEngine.Object.DestroyImmediate(combined.GetComponent<MeshFilter>().sharedMesh);
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    // Exact match on every component (Vector3 == is approximate)
    struct WeldKey : IEquatable<WeldKey>
    {
        public Vector3 p, n;
        public Vector2 uv;

        public WeldKey(Vector3 p, Vector3 n, Vector2 uv)
        {
            this.p = p;
            this.n = n;
            this.uv = uv;
        }

        public bool Equals(WeldKey o)
        {
            return p.x == o.p.x && p.y == o.p.y && p.z == o.p.z && n.x == o.n.x && n.y == o.n.y && n.z == o.n.z && uv.x == o.uv.x && uv.y == o.uv.y;
        }

        public override int GetHashCode()
        {
            return ((p.x.GetHashCode() * 31 + p.y.GetHashCode()) * 31 + p.z.GetHashCode()) * 31 + (n.GetHashCode() ^ uv.GetHashCode());
        }
    }

    static void Write(BinaryWriter w, Vector3 v)
    {
        w.Write(v.x);
        w.Write(v.y);
        w.Write(v.z);
    }

    static void Write(BinaryWriter w, Matrix4x4 m)
    {
        for (int i = 0; i < 16; i++)
            w.Write(m[i]);
    }
}
