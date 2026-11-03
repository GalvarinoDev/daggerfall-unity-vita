// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Dungeons and castles are draw bound: besides the combined block models, every door, lever, banner, exit door and click-able model
// is its own renderer.
// So after a dungeon is laid out (and VitaAtlas gave each model one atlas draw), each block's standalone models are merged per material
// into one mesh (copies of their vertices in block space; colliders and GameObjects stay, only their renderers go off).
// A model that moves (door opening, lever, platform) leaves the merged mesh the frame its transform changes (its triangles become
// degenerate, its own renderer comes back) and returns when it is back where it was built. Left alone: the combined block models,
// animated materials, disabled renderers. The meshes belong to this component and go with the dungeon.
// settings.ini [Video] VitaModelBatch = False turns it off.

#if UNITY_PSP2
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;

public class VitaModelBatch : MonoBehaviour
{
    const int maxVertices = 65000;              // 16-bit indices
    public static bool KeepUpstream;

    readonly List<Batch> batches = new List<Batch>();
    readonly List<Item> items = new List<Item>();
    bool shown = true;

    class Batch
    {
        public Transform block;
        public Mesh mesh;
        public MeshRenderer renderer;
        public List<int>[] built;               // per material, as built
        public List<int>[] triangles;           // per material, as drawn (moved models' triangles degenerate)
        public bool dirty;
    }

    class Item
    {
        public Transform transform;
        public MeshRenderer renderer;
        public Batch batch;
        public Matrix4x4 built;                 // block-space matrix when merged
        public int[] submesh, start, count;     // the item's index ranges in batch.triangles[submesh]
        public bool merged;
    }

    /// <summary>Merges the standalone models of root's blocks per material.</summary>
    public static void Build(GameObject root)
    {
        if (!DaggerfallUnity.Settings.VitaModelBatch)
            return;
        float started = Time.realtimeSinceStartup;
        var perBlock = new Dictionary<Transform, List<MeshRenderer>>();
        int models = 0;
        foreach (DaggerfallMesh dfMesh in root.GetComponentsInChildren<DaggerfallMesh>())
        {
            models++;
            MeshRenderer renderer = dfMesh.GetComponent<MeshRenderer>();
            MeshFilter filter = dfMesh.GetComponent<MeshFilter>();
            Transform block = BlockOf(dfMesh.transform, root.transform);
            if (!renderer || !renderer.enabled || !filter || !filter.sharedMesh || !filter.sharedMesh.isReadable || !block ||
                dfMesh.name == "CombinedModels" || dfMesh.GetComponent<AnimatedMaterial>() ||
                filter.sharedMesh.subMeshCount != renderer.sharedMaterials.Length)
                continue;
            List<MeshRenderer> list;
            if (!perBlock.TryGetValue(block, out list))
                perBlock.Add(block, list = new List<MeshRenderer>());
            list.Add(renderer);
        }

        VitaModelBatch owner = root.AddComponent<VitaModelBatch>();
        int draws = 0;
        foreach (var block in perBlock)
            if (block.Value.Count >= 2)
                draws += owner.Merge(block.Key, block.Value);
        foreach (Item item in owner.items)
            item.transform.hasChanged = false;
        Debug.LogFormat("VitaModelBatch: {0} of {1} models in {2} batches, {3} draws, {4:F0} ms", owner.items.Count, models,
            owner.batches.Count, draws, (Time.realtimeSinceStartup - started) * 1000f);
    }

    /// <summary>The block (root's child) that t sits in, or null.</summary>
    public static Transform BlockOf(Transform t, Transform root)
    {
        for (; t && t.parent; t = t.parent)
            if (t.parent == root)
                return t;
        return null;
    }

    // One or more batches for a block (a new one when 16-bit indices would overflow); returns the draws added
    int Merge(Transform block, List<MeshRenderer> renderers)
    {
        int draws = 0;
        int first = 0;
        while (first < renderers.Count)
        {
            int vertices = 0, last = first;
            while (last < renderers.Count && vertices + renderers[last].GetComponent<MeshFilter>().sharedMesh.vertexCount <= maxVertices)
                vertices += renderers[last++].GetComponent<MeshFilter>().sharedMesh.vertexCount;
            if (last == first)
            {
                first++;                        // one model over the limit: leave it alone
                continue;
            }
            if (last - first >= 2)
                draws += CreateBatch(block, renderers.GetRange(first, last - first));
            first = last;
        }
        return draws;
    }

    int CreateBatch(Transform block, List<MeshRenderer> renderers)
    {
        var materials = new List<Material>();
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var uv2s = new List<Vector4>();             // VitaAtlas rects: x, y, width, height
        var triangles = new List<List<int>>();
        var batch = new Batch { block = block };
        Matrix4x4 toBlock = block.worldToLocalMatrix;
        foreach (MeshRenderer renderer in renderers)
        {
            Mesh source = renderer.GetComponent<MeshFilter>().sharedMesh;
            Matrix4x4 m = toBlock * renderer.transform.localToWorldMatrix;
            int baseVertex = positions.Count;
            Vector3[] v = source.vertices;
            Vector3[] n = source.normals;
            Vector2[] uv = source.uv;
            var uv2 = new List<Vector4>();
            source.GetUVs(1, uv2);
            for (int i = 0; i < v.Length; i++)
            {
                positions.Add(m.MultiplyPoint3x4(v[i]));
                normals.Add(n.Length == v.Length ? m.MultiplyVector(n[i]).normalized : Vector3.up);
                uvs.Add(uv.Length == v.Length ? uv[i] : Vector2.zero);
                uv2s.Add(uv2.Count == v.Length ? uv2[i] : Vector4.zero);
            }
            Material[] shared = renderer.sharedMaterials;
            var item = new Item { transform = renderer.transform, renderer = renderer, batch = batch, built = m, merged = true,
                submesh = new int[shared.Length], start = new int[shared.Length], count = new int[shared.Length] };
            for (int s = 0; s < shared.Length; s++)
            {
                int index = materials.IndexOf(shared[s]);
                if (index < 0)
                {
                    index = materials.Count;
                    materials.Add(shared[s]);
                    triangles.Add(new List<int>());
                }
                int[] t = source.GetTriangles(s);
                item.submesh[s] = index;
                item.start[s] = triangles[index].Count;
                item.count[s] = t.Length;
                for (int i = 0; i < t.Length; i++)
                    triangles[index].Add(baseVertex + t[i]);
            }
            items.Add(item);
            renderer.enabled = false;
        }
        Mesh mesh = new Mesh();
        mesh.name = "VitaModelBatch";
        mesh.SetVertices(positions);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetUVs(1, uv2s);
        mesh.subMeshCount = triangles.Count;
        for (int s = 0; s < triangles.Count; s++)
            mesh.SetTriangles(triangles[s], s, false);
        mesh.RecalculateBounds();
        batch.built = triangles.ToArray();
        batch.triangles = new List<int>[triangles.Count];
        for (int s = 0; s < triangles.Count; s++)
            batch.triangles[s] = new List<int>(triangles[s]);
        batch.mesh = mesh;

        var go = new GameObject("VitaModelBatch");
        go.transform.SetParent(block, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        batch.renderer = go.AddComponent<MeshRenderer>();
        batch.renderer.sharedMaterials = materials.ToArray();
        batch.renderer.shadowCastingMode = renderers[0].shadowCastingMode;
        batch.renderer.receiveShadows = renderers[0].receiveShadows;
        batches.Add(batch);
        return materials.Count;
    }

    void LateUpdate()
    {
        if (!shown)
            return;
        // Runs after Update and coroutines, where doors and actions move: a moved model is drawn on its own from this frame on
        foreach (Item item in items)
        {
            if (!item.transform)
            {
                if (item.merged)
                    SetMerged(item, false);
                continue;
            }
            if (!item.transform.hasChanged)
                continue;
            item.transform.hasChanged = false;
            bool home = SameMatrix(item.batch.block.worldToLocalMatrix * item.transform.localToWorldMatrix, item.built);
            if (home != item.merged)
                SetMerged(item, home);
        }
        foreach (Batch batch in batches)
        {
            if (!batch.dirty)
                continue;
            batch.dirty = false;
            for (int s = 0; s < batch.triangles.Length; s++)
                batch.mesh.SetTriangles(batch.triangles[s], s, false);
        }
    }

    void SetMerged(Item item, bool merged)
    {
        item.merged = merged;
        if (item.renderer)
            item.renderer.enabled = !merged;
        for (int s = 0; s < item.submesh.Length; s++)
        {
            List<int> built = item.batch.built[item.submesh[s]];
            List<int> drawn = item.batch.triangles[item.submesh[s]];
            int start = item.start[s], end = start + item.count[s];
            // Out: every index of the model's triangles becomes its first vertex (nothing drawn); back in: the built indices
            for (int i = start; i < end; i++)
                drawn[i] = merged ? built[i] : built[start];
        }
        item.batch.dirty = true;
    }

    static bool SameMatrix(Matrix4x4 a, Matrix4x4 b)
    {
        for (int i = 0; i < 16; i++)
            if (Mathf.Abs(a[i] - b[i]) > 1e-4f)
                return false;
        return true;
    }

    public void Show(bool on)
    {
        shown = on;
        foreach (Batch batch in batches)
            batch.renderer.enabled = on;
        foreach (Item item in items)
            if (item.renderer)
                item.renderer.enabled = !on || !item.merged;
    }

    void OnDestroy()
    {
        foreach (Batch batch in batches)
            if (batch.mesh)
                Destroy(batch.mesh);
    }
}
#endif
