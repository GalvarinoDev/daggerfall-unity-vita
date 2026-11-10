// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Cities are draw-call bound: ~1400 draws at ~45 us each, block meshes ~30 of 75 ms in Daggerfall city. Each
// block's CombinedModels has one submesh per texture, so a city draws blocks x textures, and the cost of a draw is mostly the state
// that changes between draws. Unity static batching kept the draw count. So the CombinedModels of each chunk of
// VitaCityMerge x VitaCityMerge blocks are merged here into meshes with one submesh per texture key (under 65k vertices each).
// The block meshes stay for their colliders and DaggerfallMesh; only their renderer is switched off. A merged mesh has its own
// DaggerfallMesh with the same default texture keys, so ApplyClimateSettings (season, day/night windows) swaps it too. Animated
// textures animate the shared material, so they keep working. Merged meshes are owned here and destroyed with the location.

#if UNITY_PSP2
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;

public class VitaCityMerge : MonoBehaviour
{
    const int maxVertices = 65000;

    readonly List<Mesh> meshes = new List<Mesh>();

    struct Part
    {
        public int source;
        public int subMesh;
    }

    public static void Merge(GameObject location, int chunkBlocks)
    {
        DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
        if (chunkBlocks <= 0 || dfUnity.MeshReader.AddMeshLightmapUVs)
            return;

        var sources = new List<MeshFilter>();
        foreach (MeshFilter filter in location.GetComponentsInChildren<MeshFilter>())
        {
            MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
            if (filter.name == "CombinedModels" && filter.sharedMesh && filter.sharedMesh.isReadable &&
                filter.sharedMesh.vertexCount <= maxVertices && renderer && renderer.enabled &&
                filter.GetComponent<DaggerfallMesh>())
                sources.Add(filter);
        }
        if (sources.Count < 2)
            return;

        float started = Time.realtimeSinceStartup;
        GameObject root = new GameObject("VitaMergedBlocks");
        root.transform.SetParent(location.transform, false);
        VitaCityMerge owner = root.AddComponent<VitaCityMerge>();
        Matrix4x4 toRoot = root.transform.worldToLocalMatrix;

        float chunkSize = RMBLayout.RMBSide * chunkBlocks;
        var chunks = new Dictionary<long, List<MeshFilter>>();
        foreach (MeshFilter filter in sources)
        {
            Vector3 centre = toRoot.MultiplyPoint3x4(filter.GetComponent<MeshRenderer>().bounds.center);
            long key = ((long)Mathf.FloorToInt(centre.x / chunkSize) << 32) ^ (uint)Mathf.FloorToInt(centre.z / chunkSize);
            List<MeshFilter> list;
            if (!chunks.TryGetValue(key, out list))
                chunks.Add(key, list = new List<MeshFilter>());
            list.Add(filter);
        }

        int before = 0, after = 0, vertices = 0;
        if (VitaMemory.Probes)
            VitaMemory.Step("location merge start");
        foreach (List<MeshFilter> chunk in chunks.Values)
            owner.MergeChunk(chunk, toRoot, ref before, ref after, ref vertices);

        VitaMemory.Step(string.Format("location merge {0} blocks in {1} chunks: {2} -> {3} submeshes, {4} verts, {5:F0} ms",
            sources.Count, chunks.Count, before, after, vertices, (Time.realtimeSinceStartup - started) * 1000f));
    }

    void MergeChunk(List<MeshFilter> chunk, Matrix4x4 toRoot, ref int before, ref int after, ref int vertices)
    {
        // Group the chunk's submeshes by default texture key; keep the material the block has now (climate already applied)
        var groups = new Dictionary<int, List<Part>>();
        var keyOrder = new List<int>();
        var materials = new Dictionary<int, Material>();
        var used = new List<int>();
        for (int i = 0; i < chunk.Count; i++)
        {
            Mesh mesh = chunk[i].sharedMesh;
            int[] keys = chunk[i].GetComponent<DaggerfallMesh>().GetDefaultTextures();
            Material[] current = chunk[i].GetComponent<MeshRenderer>().sharedMaterials;
            if (keys.Length != mesh.subMeshCount || current.Length != mesh.subMeshCount)
                continue;
            used.Add(i);
            for (int s = 0; s < keys.Length; s++)
            {
                List<Part> parts;
                if (!groups.TryGetValue(keys[s], out parts))
                {
                    groups.Add(keys[s], parts = new List<Part>());
                    keyOrder.Add(keys[s]);
                    materials.Add(keys[s], current[s]);
                }
                Part part;
                part.source = i;
                part.subMesh = s;
                parts.Add(part);
                before++;
            }
        }
        if (used.Count < 2)
            return;

        // Source geometry in the merged root's space
        var srcVertices = new Vector3[chunk.Count][];
        var srcNormals = new Vector3[chunk.Count][];
        var srcUVs = new Vector2[chunk.Count][];
        var srcTangents = new Vector4[chunk.Count][];
        foreach (int i in used)
        {
            Mesh mesh = chunk[i].sharedMesh;
            Matrix4x4 matrix = toRoot * chunk[i].transform.localToWorldMatrix;
            Vector3[] v = mesh.vertices;
            Vector3[] n = mesh.normals;
            Vector4[] t = mesh.tangents;
            for (int k = 0; k < v.Length; k++)
            {
                v[k] = matrix.MultiplyPoint3x4(v[k]);
                n[k] = matrix.MultiplyVector(n[k]).normalized;
                if (t.Length == v.Length)
                {
                    Vector3 d = matrix.MultiplyVector(t[k]).normalized;
                    t[k] = new Vector4(d.x, d.y, d.z, t[k].w);
                }
            }
            if (t.Length == v.Length)
                srcTangents[i] = t;
            srcVertices[i] = v;
            srcNormals[i] = n;
            srcUVs[i] = mesh.uv;
        }

        if (VitaMemory.Probes)
            VitaMemory.Step("merge chunk sources read");
        var outVertices = new List<Vector3>();
        var outNormals = new List<Vector3>();
        var outUVs = new List<Vector2>();
        var outTangents = new List<Vector4>();
        var outTriangles = new List<List<int>>();
        var outKeys = new List<int>();
        DaggerfallMesh climateFrom = chunk[used[0]].GetComponent<DaggerfallMesh>();
        MeshRenderer rendererFrom = chunk[used[0]].GetComponent<MeshRenderer>();

        foreach (int key in keyOrder)
        {
            List<int> triangles = null;
            foreach (Part part in groups[key])
            {
                int[] src = chunk[part.source].sharedMesh.GetTriangles(part.subMesh);
                if (src.Length == 0)
                    continue;
                int first = int.MaxValue, last = 0;
                for (int k = 0; k < src.Length; k++)
                {
                    first = Mathf.Min(first, src[k]);
                    last = Mathf.Max(last, src[k]);
                }
                int count = last - first + 1;
                if (outVertices.Count + count > maxVertices)
                {
                    Flush(outVertices, outNormals, outUVs, outTangents, outTriangles, outKeys, materials, climateFrom, rendererFrom, ref after);
                    triangles = null;
                }
                if (triangles == null)
                {
                    outTriangles.Add(triangles = new List<int>());
                    outKeys.Add(key);
                }
                int offset = outVertices.Count - first;
                for (int k = 0; k < count; k++)
                {
                    outVertices.Add(srcVertices[part.source][first + k]);
                    outNormals.Add(srcNormals[part.source][first + k]);
                    outUVs.Add(srcUVs[part.source][first + k]);
                    outTangents.Add(srcTangents[part.source] != null ? srcTangents[part.source][first + k] : new Vector4(1, 0, 0, 1));
                }
                for (int k = 0; k < src.Length; k++)
                    triangles.Add(src[k] + offset);
                vertices += count;
            }
        }
        Flush(outVertices, outNormals, outUVs, outTangents, outTriangles, outKeys, materials, climateFrom, rendererFrom, ref after);

        foreach (int i in used)
            chunk[i].GetComponent<MeshRenderer>().enabled = false;
    }

    void Flush(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<Vector4> tangents, List<List<int>> triangles, List<int> keys,
        Dictionary<int, Material> materials, DaggerfallMesh climateFrom, MeshRenderer rendererFrom, ref int after)
    {
        if (vertices.Count > 0 && triangles.Count > 0)
        {
            Mesh mesh = new Mesh();
            mesh.name = "VitaMergedBlocks";
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            if (DaggerfallUnity.Instance.MeshReader.AddMeshTangents)
                mesh.SetTangents(tangents);
            if (VitaMemory.Probes)
                VitaMemory.Step(string.Format("merge mesh {0} verts: channels set", vertices.Count));
            mesh.subMeshCount = triangles.Count;
            var mats = new Material[triangles.Count];
            for (int s = 0; s < triangles.Count; s++)
            {
                mesh.SetTriangles(triangles[s], s);
                mats[s] = materials[keys[s]];
            }
            if (VitaMemory.Probes)
                VitaMemory.Step(string.Format("merge mesh {0} submeshes: triangles set", triangles.Count));
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);      // GPU copy only: the CPU copy is never read again
            if (VitaMemory.Probes)
                VitaMemory.Step("merge mesh uploaded");
            meshes.Add(mesh);

            GameObject go = new GameObject("Merged");
            go.transform.SetParent(transform, false);
            DaggerfallMesh dfMesh = go.AddComponent<DaggerfallMesh>();
            dfMesh.SetDefaultTextures(keys.ToArray());
            dfMesh.Climate = climateFrom.Climate;
            dfMesh.Season = climateFrom.Season;
            dfMesh.WindowStyle = climateFrom.WindowStyle;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = mats;
            renderer.shadowCastingMode = rendererFrom.shadowCastingMode;
            renderer.receiveShadows = rendererFrom.receiveShadows;
            go.isStatic = rendererFrom.gameObject.isStatic;
            after += triangles.Count;
        }
        vertices.Clear();
        normals.Clear();
        uvs.Clear();
        tangents.Clear();
        triangles.Clear();
        keys.Clear();
    }

    void OnDestroy()
    {
        foreach (Mesh mesh in meshes)
            Destroy(mesh);
        meshes.Clear();
    }
}
#endif
