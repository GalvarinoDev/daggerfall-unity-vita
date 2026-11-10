// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Windmills of Daggerfall (Kamer's mod, V2.2) built in. The Vita can't load .dfmod files, so vita/tools/windmills.py
// writes the mod's models and 7 farm blocks into Resources/VitaWindmills, and this answers DFU's own lookups for them:
// MeshReplacement asks for models 41600 (mill), 41601 (its interior gear), 41600_Desert ... and WorldDataReplacement for the blocks
// FARMAA00/01/02/05/06/07/09 (1283 farms). Textures come from Daggerfall's archives through RuntimeMaterials, as on PC.
// The mod's script dll can't run under IL2CPP: Spin below does what its Spin_Up / Spin_Desert / SpinTime_Roller do.
// settings.ini [Video] VitaWindmills (Mods window), default on. Meshes are built once and shared (owned by this class for the session).

#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility.AssetInjection;

public static class VitaWindmills
{
    class Node
    {
        public string name;
        public Vector3 position, scale, spin;
        public Quaternion rotation;
        public int mesh;
        public bool collider, sound;
        public RuntimeMaterial[] materials;
        public Node[] children;
    }

    static Dictionary<string, Node> prefabs;
    static HashSet<uint> modelIds;
    static Mesh[] meshes;

    public static bool Enabled { get { return DaggerfallUnity.Settings.VitaWindmills; } }

    /// <summary>Builds the first of names (climate variant first, as MeshReplacement orders them) the mod has.</summary>
    public static bool TryImport(uint modelId, string[] names, out GameObject go)
    {
        go = null;
        if (!Enabled || !Load() || !modelIds.Contains(modelId))
            return false;
        foreach (string name in names)
        {
            Node node;
            if (prefabs.TryGetValue(name, out node))
            {
                go = Build(node, null);
                return true;
            }
        }
        return false;
    }

    public static bool HasModel(uint modelId)
    {
        return Enabled && Load() && modelIds.Contains(modelId);
    }

    static GameObject Build(Node node, Transform parent)
    {
        GameObject go = new GameObject(node.name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = node.position;
        go.transform.localRotation = node.rotation;
        go.transform.localScale = node.scale;
        if (node.mesh >= 0)
        {
            Mesh mesh = meshes[node.mesh];
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new Material[mesh.subMeshCount];
            if (node.materials.Length > 0)
                go.AddComponent<RuntimeMaterials>().VitaApplyMaterials(node.materials);
            if (node.collider)
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }
        if (node.spin != Vector3.zero)
        {
            go.AddComponent<Spin>().degreesPerSecond = node.spin;
            if (node.sound)
                go.AddComponent<DaggerfallAudioSource>().SetSound((SoundClips)11, AudioPresets.LoopOnAwake, 1f);
        }
        foreach (Node child in node.children)
            Build(child, go.transform);
        return go;
    }

    // The mod's spin scripts: Transform.Rotate(degrees per second * deltaTime) in local space every frame
    public class Spin : MonoBehaviour
    {
        public Vector3 degreesPerSecond;

        void Update()
        {
            transform.Rotate(degreesPerSecond * Time.deltaTime, Space.Self);
        }
    }

    static bool Load()
    {
        if (prefabs != null)
            return prefabs.Count > 0;
        prefabs = new Dictionary<string, Node>();
        modelIds = new HashSet<uint>();
        TextAsset asset = Resources.Load<TextAsset>("VitaWindmills/windmills");
        if (!asset)
        {
            Debug.LogWarning("VitaWindmills: Resources/VitaWindmills/windmills.bytes missing (run vita/tools/windmills.py)");
            return false;
        }
        byte[] bytes = asset.bytes;
        Resources.UnloadAsset(asset);
        using (BinaryReader reader = new BinaryReader(new MemoryStream(bytes)))
        {
            reader.ReadBytes(4);    // "VWML"
            reader.ReadInt32();     // version
            meshes = new Mesh[reader.ReadInt32()];
            int prefabCount = reader.ReadInt32();
            for (int m = 0; m < meshes.Length; m++)
            {
                int count = reader.ReadInt32();
                Vector3[] vertices = new Vector3[count], normals = new Vector3[count];
                Vector2[] uvs = new Vector2[count];
                for (int i = 0; i < count; i++)
                    vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                for (int i = 0; i < count; i++)
                    normals[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                for (int i = 0; i < count; i++)
                    uvs[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                Mesh mesh = new Mesh();
                mesh.name = "VitaWindmills " + m;
                mesh.vertices = vertices;
                mesh.normals = normals;
                mesh.uv = uvs;
                mesh.subMeshCount = reader.ReadInt32();
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    int[] triangles = new int[reader.ReadInt32()];
                    for (int i = 0; i < triangles.Length; i++)
                        triangles[i] = reader.ReadUInt16();
                    mesh.SetTriangles(triangles, s);
                }
                mesh.RecalculateBounds();
                // Kept readable: MeshCollider cooks from the CPU copy at runtime, so a non-readable mesh gives no collision
                // (fell through a mill's interior floor near Wayrest). 9 small meshes.
                mesh.UploadMeshData(false);
                meshes[m] = mesh;
            }
            for (int p = 0; p < prefabCount; p++)
            {
                Node node = ReadNode(reader);
                prefabs[node.name] = node;
                uint id;
                if (uint.TryParse(node.name.Split('_')[0], out id))
                    modelIds.Add(id);
            }
        }
        Debug.LogFormat("VitaWindmills: {0} meshes, {1} models", meshes.Length, prefabs.Count);
        return true;
    }

    static Node ReadNode(BinaryReader reader)
    {
        Node node = new Node();
        node.name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
        node.position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        node.rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        node.scale = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        node.mesh = reader.ReadInt32();
        node.collider = reader.ReadByte() != 0;
        node.materials = new RuntimeMaterial[reader.ReadInt32()];
        for (int i = 0; i < node.materials.Length; i++)
        {
            node.materials[i].Index = reader.ReadInt32();
            node.materials[i].Archive = reader.ReadInt32();
            node.materials[i].Record = reader.ReadInt32();
            node.materials[i].ApplyClimate = reader.ReadByte() != 0;
        }
        node.spin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        node.sound = reader.ReadByte() != 0;
        node.children = new Node[reader.ReadInt32()];
        for (int i = 0; i < node.children.Length; i++)
            node.children[i] = ReadNode(reader);
        return node;
    }
}
#endif
