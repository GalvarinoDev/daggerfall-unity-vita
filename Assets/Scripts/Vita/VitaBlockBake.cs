// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked city/village blocks. The editor baker (Assets/Editor/Vita/VitaBlockBaker.cs) runs DFU's own
// RMBLayout.CreateBaseGameObject on the PC for every RMB block of BLOCKS.BSA and of the built-in world-data packs, and writes what it
// built into StreamingAssets/VitaNative/blocks.pak (records deflated: 296 -> 34 MB, so fewer bytes from the card too): the CombinedModels data as ModelCombiner.Apply() made it, the static doors and
// buildings, and the standalone models (gates, bulletin boards). RMBLayout then builds a block from its record instead of reading
// every model from ARCH3D and merging them (~16 s per city, mp1). Records are keyed by block name + a hash of the block's model records,
// runtime, in their place in the hierarchy. A block with another mod model or a model custom activation falls back to DFU's path. Every
// fallback logs "VITANATIVE miss block".

#if UNITY_PSP2
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Utility.AssetInjection;

public static class VitaBlockBake
{
    public const string Magic = "VBLK";
    public const int Version = 4;   // 4: standalone entries with ReplacementFlag (windmill models, imported at runtime)
    public const int ReplacementFlag = 1 << 30;
    public const int Align = 512;

    static Dictionary<string, int[]> index;     // name/hash -> offset, deflated length, length
    static FileStream file;                     // app0: (not invalidated by suspend), one fd for the session
    static byte[] deflated = new byte[0], buffer = new byte[0];   // reused: the IL2CPP heap never shrinks
    static int pos;

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "blocks.pak"); }
    }

    public static string Key(string blockName, uint hash)
    {
        return blockName + "/" + hash.ToString("x8");
    }

    #region Hash (shared with the baker)

    [StructLayout(LayoutKind.Explicit)]
    struct FloatBits
    {
        [FieldOffset(0)] public float f;
        [FieldOffset(0)] public int i;
    }

    // FNV-1a over everything RMBLayout.AddModels/AddProps read from the block
    public static uint Hash(ref DFBlock block)
    {
        uint h = 2166136261;
        DFBlock.RmbSubRecord[] subRecords = block.RmbBlock.SubRecords;
        h = Mix(h, subRecords == null ? -1 : subRecords.Length);
        if (subRecords != null)
        {
            foreach (DFBlock.RmbSubRecord sub in subRecords)
            {
                h = Mix(h, sub.XPos);
                h = Mix(h, sub.ZPos);
                h = Mix(h, sub.YRotation);
                h = Objects(h, sub.Exterior.Block3dObjectRecords);
            }
        }
        return Objects(h, block.RmbBlock.Misc3dObjectRecords);
    }

    static uint Objects(uint h, DFBlock.RmbBlock3dObjectRecord[] objects)
    {
        h = Mix(h, objects == null ? -1 : objects.Length);
        if (objects == null)
            return h;
        FloatBits bits = new FloatBits();
        foreach (DFBlock.RmbBlock3dObjectRecord obj in objects)
        {
            h = Mix(h, (int)obj.ModelIdNum);
            h = Mix(h, obj.XPos);
            h = Mix(h, obj.YPos);
            h = Mix(h, obj.ZPos);
            h = Mix(h, obj.XRotation);
            h = Mix(h, obj.YRotation);
            h = Mix(h, obj.ZRotation);
            bits.f = obj.XScale; h = Mix(h, bits.i);
            bits.f = obj.YScale; h = Mix(h, bits.i);
            bits.f = obj.ZScale; h = Mix(h, bits.i);
        }
        return h;
    }

    // FNV-1a over everything RDBLayout.AddModels reads to decide and place the combined models; the exit door choice is part of it
    public static uint RdbHash(ref DFBlock block, bool allowExitDoors)
    {
        uint h = Mix(2166136261, allowExitDoors ? 1 : 0);
        DFBlock.RdbObjectRoot[] roots = block.RdbBlock.ObjectRootList;
        h = Mix(h, roots == null ? -1 : roots.Length);
        if (roots == null)
            return h;
        foreach (DFBlock.RdbObjectRoot root in roots)
        {
            h = Mix(h, root.RdbObjects == null ? -1 : root.RdbObjects.Length);
            if (root.RdbObjects == null)
                continue;
            foreach (DFBlock.RdbObject obj in root.RdbObjects)
            {
                if (obj.Type != DFBlock.RdbResourceTypes.Model)
                    continue;
                int reference = obj.Resources.ModelResource.ModelIndex;
                h = Mix(h, reference);
                h = Mix(h, (int)block.RdbBlock.ModelReferenceList[reference].ModelIdNum);
                foreach (char c in block.RdbBlock.ModelReferenceList[reference].Description ?? "")
                    h = Mix(h, c);
                h = Mix(h, obj.XPos);
                h = Mix(h, obj.YPos);
                h = Mix(h, obj.ZPos);
                h = Mix(h, obj.Resources.ModelResource.XRotation);
                h = Mix(h, obj.Resources.ModelResource.YRotation);
                h = Mix(h, obj.Resources.ModelResource.ZRotation);
                h = Mix(h, obj.Resources.ModelResource.ActionResource.Flags != 0 ? 1 : 0);
            }
        }
        return h;
    }

    static uint Mix(uint h, int v)
    {
        for (int i = 0; i < 4; i++, v >>= 8)
            h = (h ^ (uint)(v & 0xff)) * 16777619;
        return h;
    }

    #endregion

    #region Runtime

    /// <summary>
    /// Builds the block's models, doors and buildings into go from the bake, exactly as RMBLayout.CreateBaseGameObject would.
    /// False (and nothing added) when there is no matching record or the block needs DFU's path (mod models, custom activations).
    /// </summary>
    // In-play town builds: the card read + inflate on a frame of its own, 40-75 ms per block in Wayrest's neighbour. Whatever reads next
    // overwrites the buffer and bufferOffset, so a stale prefetch only means TryBuild reads again.
    static int bufferOffset = -1;
    public static void Prefetch(ref DFBlock blockData)
    {
        int[] entry;
        if (DaggerfallUnity.Instance.Option_CombineRMB && NeedsRuntimePath(ref blockData) == null && Index().TryGetValue(Key(blockData.Name, Hash(ref blockData)), out entry))
            Read(entry[0], entry[1], entry[2]);
    }

    public static bool TryBuild(ref DFBlock blockData, int layoutX, int layoutY, GameObject go)
    {
        DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
        if (!dfUnity.Option_CombineRMB)
            return false;

        uint hash = Hash(ref blockData);
        string reason = NeedsRuntimePath(ref blockData);
        int[] entry = null;
        if (reason == null && !Index().TryGetValue(Key(blockData.Name, hash), out entry))
            reason = "not baked";
        if (reason != null)
        {
            Debug.LogFormat("VITANATIVE miss block {0} {1:x8} ({2})", blockData.Name, hash, reason);
            return false;
        }

        if (bufferOffset == entry[0])
            pos = 0;    // Prefetch read it on an earlier frame
        else
            Read(entry[0], entry[1], entry[2]);

        int vertexCount;
        ModelCombiner.CombinedModel combined = ReadCombined(out vertexCount);

        // Doors and buildings, keys assigned as CreateBaseGameObject does
        StaticDoor[] doors = new StaticDoor[Int()];
        for (int i = 0; i < doors.Length; i++)
        {
            doors[i].buildingMatrix = Matrix();
            doors[i].doorType = (DoorTypes)Int();
            doors[i].recordIndex = Int();
            doors[i].doorIndex = Int();
            doors[i].centre = Vec3();
            doors[i].size = Vec3();
            doors[i].normal = Vec3();
            doors[i].blockIndex = blockData.Index;
            doors[i].buildingKey = BuildingDirectory.MakeBuildingKey((byte)layoutX, (byte)layoutY, (byte)doors[i].recordIndex);
        }
        StaticBuilding[] buildings = new StaticBuilding[Int()];
        for (int i = 0; i < buildings.Length; i++)
        {
            buildings[i].modelMatrix = Matrix();
            buildings[i].recordIndex = Int();
            buildings[i].centre = Vec3();
            buildings[i].size = Vec3();
            buildings[i].buildingKey = BuildingDirectory.MakeBuildingKey((byte)layoutX, (byte)layoutY, (byte)buildings[i].recordIndex);
        }

        // Same hierarchy and order as CreateBaseGameObject: Models node, standalone models, doors, buildings, CombinedModels
        GameObject modelsNode = new GameObject("Models");
        modelsNode.transform.parent = go.transform;
        int standalones = Int();
        for (int i = 0; i < standalones; i++)
        {
            int raw = Int();
            uint modelID = (uint)(raw & ~ReplacementFlag);
            if ((raw & ReplacementFlag) != 0)
            {
                Vector3 position = Vec3();
                Quaternion rotation = new Quaternion(Float(), Float(), Float(), Float());
                Vector3 scale = Vec3();
                if (!MeshReplacement.ImportCustomGameobject(modelID, modelsNode.transform, Matrix4x4.TRS(position, rotation, scale)))
                    Debug.LogFormat("VITANATIVE miss block {0}: replacement model {1} not available", blockData.Name, modelID);
                continue;
            }
            // RMBLayout.AddStandaloneModel
            GameObject model = GameObjectHelper.CreateDaggerfallMeshGameObject(modelID, modelsNode.transform, dfUnity.Option_SetStaticFlags);
            model.transform.position = Vec3();
            model.transform.rotation = new Quaternion(Float(), Float(), Float(), Float());
            model.transform.localScale = Vec3();
            if (modelID == RMBLayout.CityGateOpenModelID || modelID == RMBLayout.CityGateClosedModelID)
                model.AddComponent<DaggerfallCityGate>();
            if (modelID == RMBLayout.BulletinBoardModelID)
                model.AddComponent<DaggerfallBulletinBoard>();
        }
        if (doors.Length > 0)
        {
            DaggerfallStaticDoors c = go.GetComponent<DaggerfallStaticDoors>();
            if (c == null)
                c = go.AddComponent<DaggerfallStaticDoors>();
            c.Doors = doors;
        }
        if (buildings.Length > 0)
        {
            DaggerfallStaticBuildings c = go.GetComponent<DaggerfallStaticBuildings>();
            if (c == null)
                c = go.AddComponent<DaggerfallStaticBuildings>();
            c.Buildings = buildings;
        }
        if (vertexCount > 0)
        {
            ModelCombiner combiner = new ModelCombiner();
            combiner.VitaSetCombined(combined);
            GameObjectHelper.CreateCombinedMeshGameObject(combiner, "CombinedModels", modelsNode.transform, dfUnity.Option_SetStaticFlags);
        }
        return true;
    }

    /// <summary>
    /// Dungeon blocks: seals combiner with the block's baked combined models, so RDBLayout.AddModels adds only the models it doesn't combine
    /// (action models, exit doors, tapestries) and its Apply() does nothing. False (combiner untouched) without a matching record or when a
    /// model needs DFU's path (mod models, custom activations).
    /// </summary>
    public static bool TrySealRdb(ref DFBlock blockData, bool allowExitDoors, ModelCombiner combiner)
    {
        uint hash = RdbHash(ref blockData, allowExitDoors);
        string reason = NeedsRuntimePathRdb(ref blockData);
        int[] entry = null;
        if (reason == null && !Index().TryGetValue(Key(blockData.Name, hash), out entry))
            reason = "not baked";
        if (reason != null)
        {
            Debug.LogFormat("VITANATIVE miss block {0} {1:x8} ({2})", blockData.Name, hash, reason);
            return false;
        }
        Read(entry[0], entry[1], entry[2]);
        int vertexCount;
        combiner.VitaSetCombined(ReadCombined(out vertexCount));
        return true;
    }

    // Same decisions as RDBLayout.AddModels makes per model
    static string NeedsRuntimePathRdb(ref DFBlock blockData)
    {
        if (blockData.RdbBlock.ObjectRootList != null)
            foreach (DFBlock.RdbObjectRoot root in blockData.RdbBlock.ObjectRootList)
                if (root.RdbObjects != null)
                    foreach (DFBlock.RdbObject obj in root.RdbObjects)
                    {
                        if (obj.Type != DFBlock.RdbResourceTypes.Model)
                            continue;
                        uint modelId = blockData.RdbBlock.ModelReferenceList[obj.Resources.ModelResource.ModelIndex].ModelIdNum;
                        if (MeshReplacement.VitaHasCustomGameobject(modelId))
                            return "mod model " + modelId;
                        if (PlayerActivate.HasCustomActivation(modelId))
                            return "custom activation " + modelId;
                    }
        return null;
    }

    // The record's combined model section, as ModelCombiner.Apply() made it
    static ModelCombiner.CombinedModel ReadCombined(out int vertexCount)
    {
        ModelCombiner.CombinedModel combined = new ModelCombiner.CombinedModel();
        vertexCount = Int();
        combined.SubMeshes = new ModelCombiner.SubMesh[Int()];
        for (int i = 0; i < combined.SubMeshes.Length; i++)
        {
            combined.SubMeshes[i].TextureArchive = Int();
            combined.SubMeshes[i].TextureRecord = Int();
            combined.SubMeshes[i].StartIndex = Int();
            combined.SubMeshes[i].PrimitiveCount = Int();
        }
        combined.Vertices = new Vector3[vertexCount];
        combined.Normals = new Vector3[vertexCount];
        combined.UVs = new Vector2[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            combined.Vertices[i] = Vec3();
        for (int i = 0; i < vertexCount; i++)
            combined.Normals[i] = Vec3();
        for (int i = 0; i < vertexCount; i++)
            combined.UVs[i] = new Vector2(Float(), Float());
        // Apply() makes one vertex per index; the baker welds identical ones inside each submesh (same triangles)
        combined.Indices = new int[Int()];
        for (int i = 0; i < combined.Indices.Length; i++)
            combined.Indices[i] = Int();
        return combined;
    }

    // Why DFU's own path must build this block, or null. Same decisions as AddModels/AddProps make per model.
    static string NeedsRuntimePath(ref DFBlock blockData)
    {
        if (blockData.RmbBlock.SubRecords != null)
            foreach (DFBlock.RmbSubRecord sub in blockData.RmbBlock.SubRecords)
            {
                string reason = NeedsRuntimePath(sub.Exterior.Block3dObjectRecords);
                if (reason != null)
                    return reason;
            }
        return NeedsRuntimePath(blockData.RmbBlock.Misc3dObjectRecords);
    }

    static string NeedsRuntimePath(DFBlock.RmbBlock3dObjectRecord[] objects)
    {
        if (objects != null)
            foreach (DFBlock.RmbBlock3dObjectRecord obj in objects)
            {
#if !VITA_MODS
                if (MeshReplacement.VitaHasCustomGameobject(obj.ModelIdNum))
#else
                if (!VitaWindmills.HasModel(obj.ModelIdNum) && MeshReplacement.VitaHasCustomGameobject(obj.ModelIdNum))
#endif
                    return "mod model " + obj.ModelIdNum;
                if (PlayerActivate.HasCustomActivation(obj.ModelIdNum))
                    return "custom activation " + obj.ModelIdNum;
            }
        return null;
    }

    static Dictionary<string, int[]> Index()
    {
        if (index != null)
            return index;
        index = new Dictionary<string, int[]>();
        try
        {
            file = new FileStream(PackPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            BinaryReader reader = new BinaryReader(file);
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != Magic || reader.ReadInt32() != Version)
                throw new IOException("not a version " + Version + " pack");
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = Encoding.ASCII.GetString(reader.ReadBytes(reader.ReadInt32()));
                uint hash = reader.ReadUInt32();
                index[Key(name, hash)] = new[] { reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() };
            }
            Debug.LogFormat("VitaBlockBake: {0} blocks", count);
        }
        catch (Exception e)
        {
            Debug.LogWarningFormat("VitaBlockBake: {0} not loaded: {1}", PackPath, e.Message);
            index.Clear();
        }
        return index;
    }

    // One read per record, then inflate
    static void Read(int offset, int deflatedLength, int length)
    {
        if (deflated.Length < deflatedLength)
            deflated = new byte[deflatedLength];
        if (buffer.Length < length)
            buffer = new byte[length];
        file.Seek(offset, SeekOrigin.Begin);
        ReadAll(file, deflated, deflatedLength);
        bufferOffset = -1;
        using (DeflateStream inflate = new DeflateStream(new MemoryStream(deflated, 0, deflatedLength), CompressionMode.Decompress))
            ReadAll(inflate, buffer, length);
        bufferOffset = offset;
        pos = 0;
    }

    static void ReadAll(Stream stream, byte[] to, int length)
    {
        int done = 0, n;
        while (done < length && (n = stream.Read(to, done, length - done)) > 0)
            done += n;
        if (done != length)
            throw new IOException("VitaBlockBake: short read");
    }

    static int Int()
    {
        int v = BitConverter.ToInt32(buffer, pos);
        pos += 4;
        return v;
    }

    static float Float()
    {
        float v = BitConverter.ToSingle(buffer, pos);
        pos += 4;
        return v;
    }

    static Vector3 Vec3()
    {
        return new Vector3(Float(), Float(), Float());
    }

    static Matrix4x4 Matrix()
    {
        Matrix4x4 m = new Matrix4x4();
        for (int i = 0; i < 16; i++)
            m[i] = Float();
        return m;
    }

    #endregion
}
#endif
