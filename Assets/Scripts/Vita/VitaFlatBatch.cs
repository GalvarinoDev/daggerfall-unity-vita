// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Dungeons and castles are draw bound (~110-160 us of render work per draw, plus per-object light culling for each renderer): inside the
// Palace ~110 of ~380 draws are plain flats (torches, candles, decorations), each its own billboard.
// So after a dungeon is laid out, the plain flats of each block that share a material become one mesh: each quad keeps its pivot as the
// vertex position and its corner offset/normal in tangent/normal, and Daggerfall/Vita/FlatBatchPalette turns them to the camera in the
// vertex shader exactly as DaggerfallBillboard's shared facing does (global _VitaFacing, set here every frame). Same material textures and
// shader body as Daggerfall/Vita/BillboardPalette. The billboards stay (renderer off, animation timers still running): animated flats'
// current frames are copied into the batch's UVs.
// Left alone: anything with a collider, an action, other components, editor/NPC flats, FaceY, OneShot, imported textures.
// The meshes and materials belong to this component and go with the dungeon. settings.ini [Video] VitaFlatBatch = False turns it off.

#if UNITY_PSP2
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;
using DaggerfallConnect.Arena2;

public class VitaFlatBatch : MonoBehaviour
{
    public const string ShaderName = "Daggerfall/Vita/FlatBatchPalette";
    public static bool KeepUpstream;
    static readonly int facingId = Shader.PropertyToID("_VitaFacing");

    static readonly int[] rejected = new int[6];   // per reason in Eligible, logged by Build
    static readonly Dictionary<string, int> rejectDetail = new Dictionary<string, int>();  // what blocked them (components, parents)
    readonly List<Batch> batches = new List<Batch>();
    readonly List<Material> materials = new List<Material>();
    Camera mainCamera;
    bool shown = true;

    class Batch
    {
        public MeshRenderer renderer;
        public Mesh mesh;
        public Vector2[] uvs;
        public List<Item> items = new List<Item>();
        public List<Item> animated = new List<Item>();
    }

    class Item
    {
        public DaggerfallBillboard billboard;
        public MeshRenderer renderer;
        public int firstVertex;
        public int shownFrame;
    }

    /// <summary>Merges root's plain flats per block and material into one camera-facing mesh each.</summary>
    public static void Build(GameObject root)
    {
        if (!DaggerfallUnity.Settings.VitaFlatBatch || !Shader.Find(ShaderName))
            return;
        float started = Time.realtimeSinceStartup;
        var groups = new Dictionary<KeyValuePair<Transform, Material>, List<DaggerfallBillboard>>();
        int flats = 0;
        System.Array.Clear(rejected, 0, rejected.Length);
        rejectDetail.Clear();
        foreach (DaggerfallBillboard billboard in root.GetComponentsInChildren<DaggerfallBillboard>())
        {
            flats++;
            MeshRenderer renderer;
            Transform block;
            if (!Eligible(billboard, root.transform, out renderer, out block))
                continue;
            var key = new KeyValuePair<Transform, Material>(block, renderer.sharedMaterial);
            List<DaggerfallBillboard> list;
            if (!groups.TryGetValue(key, out list))
                groups.Add(key, list = new List<DaggerfallBillboard>());
            list.Add(billboard);
        }

        VitaFlatBatch owner = root.AddComponent<VitaFlatBatch>();
        var copies = new Dictionary<Material, Material>();
        int merged = 0;
        foreach (var group in groups)
        {
            if (group.Value.Count < 2)
                continue;
            Material material;
            if (!copies.TryGetValue(group.Key.Value, out material))
            {
                Material source = group.Key.Value;
                material = new Material(Shader.Find(ShaderName));
                material.CopyPropertiesFromMaterial(source);
                material.shaderKeywords = source.shaderKeywords;
                material.renderQueue = source.renderQueue;
                copies.Add(source, material);
                owner.materials.Add(material);
            }
            owner.batches.Add(owner.CreateBatch(group.Key.Key, material, group.Value));
            merged += group.Value.Count;
        }
        Debug.LogFormat("VitaFlatBatch: {0} of {1} flats in {2} batches ({3} materials), {4:F0} ms; left: type {5}, components/action {6}, " +
            "renderer/material {7}, scale {8}, no block {9}", merged, flats, owner.batches.Count, owner.materials.Count,
            (Time.realtimeSinceStartup - started) * 1000f, rejected[0], rejected[1], rejected[2], rejected[3], rejected[4]);
        foreach (var detail in rejectDetail)
            Debug.LogFormat("VitaFlatBatch: left {0} x{1}", detail.Key, detail.Value);
    }

    static bool Eligible(DaggerfallBillboard billboard, Transform root, out MeshRenderer renderer, out Transform block)
    {
        renderer = null;
        block = null;
        BillboardSummary summary = billboard.Summary;
        if (billboard.FaceY || billboard.OneShot || !summary.AtlasedMaterial || summary.ImportedTextures.HasImportedTextures ||
            summary.FlatType == FlatTypes.Editor || summary.FlatType == FlatTypes.NPC)
            return Reject(0, string.Format("type: {0}{1}{2}{3}{4}", summary.FlatType, billboard.FaceY ? " FaceY" : "",
                billboard.OneShot ? " OneShot" : "", summary.AtlasedMaterial ? "" : " not atlased",
                summary.ImportedTextures.HasImportedTextures ? " imported" : ""));
        // Only Transform + MeshFilter + MeshRenderer + DaggerfallBillboard (+ a torch's sound), nothing that moves it (actions sit on the
        // flat or a parent)
        Component[] components = billboard.GetComponents<Component>();
        DaggerfallAction action = billboard.GetComponentInParent<DaggerfallAction>();
        int extra = 0;
        foreach (Component c in components)
            if (!(c is Transform || c is MeshFilter || c is MeshRenderer || c is DaggerfallBillboard || c is AudioSource || c is DaggerfallAudioSource))
                extra++;
        if (extra > 0 || action)
        {
            string what = action ? "action on " + (action.gameObject == billboard.gameObject ? "flat" : "parent " + action.name) : "";
            foreach (Component c in components)
                if (!(c is Transform || c is MeshFilter || c is MeshRenderer || c is DaggerfallBillboard))
                    what += " +" + c.GetType().Name;
            return Reject(1, "components/action:" + what);
        }
        renderer = billboard.GetComponent<MeshRenderer>();
        MeshFilter filter = billboard.GetComponent<MeshFilter>();
        if (!renderer || !renderer.enabled || !filter || !filter.sharedMesh || filter.sharedMesh.vertexCount != 4 ||
            !renderer.sharedMaterial || renderer.sharedMaterial.shader.name != VitaPalette.BillboardShaderName)
            return Reject(2);
        Vector3 scale = billboard.transform.lossyScale;
        if (Mathf.Abs(scale.x - scale.y) > 1e-4f || Mathf.Abs(scale.x - scale.z) > 1e-4f)
            return Reject(3);
        // The block is root's child
        block = VitaModelBatch.BlockOf(billboard.transform, root);
        if (!block)
        {
            string path = "";
            for (Transform p = billboard.transform.parent; p; p = p.parent)
                path = p.name + "/" + path;
            return Reject(4, "no block: " + path + " type " + summary.FlatType);
        }
        return true;
    }

    static bool Reject(int reason, string detail = null)
    {
        rejected[reason]++;
        if (detail != null)
        {
            int count;
            rejectDetail.TryGetValue(detail, out count);
            rejectDetail[detail] = count + 1;
        }
        return false;
    }

    Batch CreateBatch(Transform block, Material material, List<DaggerfallBillboard> billboards)
    {
        var go = new GameObject("VitaFlatBatch");
        go.transform.SetParent(block, false);
        Transform t = go.transform;
        int n = billboards.Count;
        var vertices = new Vector3[n * 4];
        var tangents = new Vector4[n * 4];
        var normals = new Vector3[n * 4];
        var uvs = new Vector2[n * 4];
        var triangles = new int[n * 6];
        var batch = new Batch { uvs = uvs };
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        float reach = 0, height = 0;
        for (int i = 0; i < n; i++)
        {
            DaggerfallBillboard billboard = billboards[i];
            Mesh quad = billboard.GetComponent<MeshFilter>().sharedMesh;
            Vector3[] qv = quad.vertices;
            Vector3[] qn = quad.normals;
            Vector2[] qu = quad.uv;
            int[] qt = quad.triangles;
            float scale = billboard.transform.lossyScale.x;
            Vector3 pivot = t.InverseTransformPoint(billboard.transform.position);
            min = Vector3.Min(min, pivot);
            max = Vector3.Max(max, pivot);
            int v0 = i * 4;
            for (int v = 0; v < 4; v++)
            {
                Vector3 corner = qv[v] * scale;
                vertices[v0 + v] = pivot;
                tangents[v0 + v] = new Vector4(corner.x, corner.y, corner.z, 0);
                normals[v0 + v] = qn[v];
                uvs[v0 + v] = qu[v];
                reach = Mathf.Max(reach, new Vector2(corner.x, corner.z).magnitude);
                height = Mathf.Max(height, Mathf.Abs(corner.y));
            }
            for (int k = 0; k < 6; k++)
                triangles[i * 6 + k] = v0 + qt[k];
            var item = new Item { billboard = billboard, renderer = billboard.GetComponent<MeshRenderer>(), firstVertex = v0, shownFrame = -1 };
            batch.items.Add(item);
            if (billboard.Summary.AnimatedMaterial)
                batch.animated.Add(item);
            item.renderer.enabled = false;
        }
        MeshRenderer first = billboards[0].GetComponent<MeshRenderer>();
        MakeMesh(go, batch, material, vertices, tangents, normals, triangles, min, max, reach, height, first.shadowCastingMode, first.receiveShadows);
        return batch;
    }

    static void MakeMesh(GameObject go, Batch batch, Material material, Vector3[] vertices, Vector4[] tangents, Vector3[] normals, int[] triangles,
        Vector3 min, Vector3 max, float reach, float height, UnityEngine.Rendering.ShadowCastingMode shadows, bool receiveShadows)
    {
        Mesh mesh = new Mesh();
        mesh.name = "VitaFlatBatch";
        mesh.vertices = vertices;
        mesh.tangents = tangents;
        mesh.normals = normals;
        mesh.uv = batch.uvs;
        mesh.triangles = triangles;
        // The quads turn around their pivots: bound them for any facing
        Vector3 grow = new Vector3(reach, height, reach);
        mesh.bounds = new Bounds((min + max) * 0.5f, max - min + grow * 2);
        if (batch.animated.Count == 0)
            mesh.UploadMeshData(true);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        batch.renderer = go.AddComponent<MeshRenderer>();
        batch.renderer.sharedMaterial = material;
        batch.renderer.shadowCastingMode = shadows;
        batch.renderer.receiveShadows = receiveShadows;
        batch.mesh = mesh;
    }

    // Towns: a city block's plain flats go straight into batches at layout, with no billboard objects at all
    // (Daggerfall city: 492 exterior flats took 3.5 s as billboards, atp2; each also ran Update every frame). Same quads as
    // MeshReader.GetBillboardMesh + AlignToBase, same shader and facing as the dungeon batches. Left as billboards: NPCs, animals,
    // lights, nature (climate swaps), editor markers, animated flats, flats with an activation or imported textures.
    struct CityFlat
    {
        public Vector3 pivot;   // world, block not moved yet
        public Vector2 half;
        public Rect rect;
    }
    static readonly Dictionary<Material, List<CityFlat>> pendingCity = new Dictionary<Material, List<CityFlat>>();
    static readonly Dictionary<Material, Material> cityMaterials = new Dictionary<Material, Material>();   // atlas material -> batch copy
    static readonly Dictionary<int, bool> imported = new Dictionary<int, bool>();

    /// <summary>Queues a city flat for this block's batch (BuildCityBlock); false = make the billboard as usual.</summary>
    /// sizeRecord: the record whose size the quad takes (-1 = its own); anyType: also nature archives (fixed records, no climate swap).
    public static bool AddCityFlat(int archive, int record, Vector3 position, int sizeRecord = -1, bool anyType = false)
    {
        if (!DaggerfallUnity.Settings.VitaFlatBatch || (!anyType && MaterialReader.GetFlatType(archive) != FlatTypes.Decoration) || RDBLayout.IsNPCFlat(archive) ||
            DaggerfallWorkshop.Game.PlayerActivate.HasCustomActivation(archive, record) || HasImportedTexture(archive, record))
            return false;
        MaterialReader reader = DaggerfallUnity.Instance.MaterialReader;
        Rect[] rects;
        RecordIndex[] indices;
        Material source = reader.GetMaterialAtlas(archive, 0, 4, 2048, out rects, out indices, 4, true, 0, false, true);
        CachedMaterial cm;
        if (!source || source.shader.name != VitaPalette.BillboardShaderName || record < 0 || record >= indices.Length ||
            indices[record].frameCount != 1 || !reader.GetCachedMaterialAtlas(archive, out cm) || cm.keyGroup != MaterialReader.AtlasKeyGroup ||
            sizeRecord >= indices.Length)
            return false;
        // MeshReader.GetBillboardMesh's size, then Billboard.AlignToBase
        if (sizeRecord < 0)
            sizeRecord = record;
        Vector2 size = cm.recordSizes[sizeRecord], scale = cm.recordScales[sizeRecord];
        Vector2 final;
        final.x = size.x + (int)(size.x * (scale.x / BlocksFile.ScaleDivisor));
        final.y = size.y + (int)(size.y * (scale.y / BlocksFile.ScaleDivisor));
        var flat = new CityFlat
        {
            pivot = position + new Vector3(0, (final * MeshReader.GlobalScale).y / 2, 0),
            half = final / 2 * MeshReader.GlobalScale,
            rect = rects[indices[record].startIndex],
        };
        List<CityFlat> list;
        if (!pendingCity.TryGetValue(source, out list))
            pendingCity.Add(source, list = new List<CityFlat>());
        list.Add(flat);
        return true;
    }

    public const int WinterCropArchive = 511, WinterCropRecord = 22;

    /// outside desert climates. Its harvesting isn't ported, only the look. Callers make a billboard with the winter record.</summary>
    public static bool IsWinterCrop(int archive)
    {
        return false;
    }

    public static bool IsCropField(uint modelId)
    {
        return modelId >= 53211 && modelId <= 53214;
    }

    static readonly Collider[] overlaps = new Collider[32];

    /// <summary>Queues a crop field's flats for this block's batch, as RMBCropBillboardBatch lays them out at its default CropDensity (6):
    /// a jittered grid round the field's position, axis-aligned (the mod ignores the rotation), no crop within 1 m of a non-terrain
    /// collider of the block (the mod's OverlapSphere also sees neighbouring blocks; this one isn't placed yet), records by climate,
    /// winter 511/22, every crop the size of the set's last record (the mod keeps the last size). The mod's Unity Random becomes a seed per
    /// block and field, so a field looks the same on every visit. Crops the batch can't take become billboards.</summary>
    public static void AddCropField(uint modelId, Vector3 position, string blockName, Transform block, Transform flatsParent)
    {
        // Prefabs 53211-53214: textureArchive 301, initialTextureRecord 0, gridDensity 0.25, noiseAmount 0.5, ranges 85/35
        int rangeX = modelId == 53212 || modelId == 53214 ? 35 : 85;
        int rangeY = modelId == 53213 || modelId == 53214 ? 35 : 85;
        const float gridDensity = 0.25f, noiseAmount = 0.5f;
        const int cropDensity = 6;
        int archive = 301;
        int[] records;
        if (IsWinterCrop(archive))
        {
            archive = WinterCropArchive;
            records = new[] { WinterCropRecord };
        }
        else
        {
            var gps = DaggerfallWorkshop.Game.GameManager.Instance.PlayerGPS;
            switch (gps != null ? (MapsFile.Climates)gps.CurrentClimateIndex : (MapsFile.Climates)231)
            {
                case MapsFile.Climates.Mountain: records = new[] { 0, 1 }; break;
                case MapsFile.Climates.Desert: records = new[] { 2 }; break;
                case MapsFile.Climates.Desert2: records = new[] { 20 }; break;
                case MapsFile.Climates.Subtropical: records = new[] { 3, 4 }; break;
                case MapsFile.Climates.Rainforest:
                case MapsFile.Climates.Swamp: records = new[] { 7, 8 }; break;
                default: records = new[] { 19, 21 }; break;
            }
        }
        int sizeRecord = records[records.Length - 1];

        int seed = (int)modelId;
        unchecked
        {
            foreach (char c in blockName)
                seed = seed * 31 + c;
            seed = (seed * 31 + Mathf.RoundToInt(position.x * 100)) * 31 + Mathf.RoundToInt(position.z * 100);
        }
        var random = new System.Random(seed);

        float xSpacing = rangeX / (rangeX * (gridDensity * (cropDensity * 0.1f)));
        float ySpacing = rangeY / (rangeY * (gridDensity * (cropDensity * 0.1f)));
        for (float x = -rangeX / 2; x <= rangeX / 2; x += xSpacing)
        {
            for (float z = -rangeY / 2; z <= rangeY / 2; z += ySpacing)
            {
                Vector3 p = position + new Vector3(x + (float)(random.NextDouble() * 2 - 1) * noiseAmount, 0, z + (float)(random.NextDouble() * 2 - 1) * noiseAmount);
                int record = records[random.Next(records.Length)];
                int n = Physics.OverlapSphereNonAlloc(block.TransformPoint(p), 1, overlaps);
                bool blocked = false;
                for (int i = 0; i < n && !blocked; i++)
                    blocked = !(overlaps[i] is TerrainCollider) && overlaps[i].transform.IsChildOf(block);
                if (blocked || AddCityFlat(archive, record, p, sizeRecord, true))
                    continue;
                GameObject go = GameObjectHelper.CreateDaggerfallBillboardGameObject(archive, record, flatsParent);
                go.transform.position = p;
                RMBLayout.AlignBillboardToBase(go);
            }
        }
    }

    // TextureReplacement.GetStaticBillboardMaterial would give the billboard its own material: once per record
    static bool HasImportedTexture(int archive, int record)
    {
        if (!DaggerfallUnity.Settings.AssetInjection)
            return false;
        int key = archive * 1024 + record;
        bool found;
        if (!imported.TryGetValue(key, out found))
        {
            Texture2D texture;
            found = DaggerfallWorkshop.Utility.AssetInjection.TextureReplacement.TryImportTexture(archive, record, 0, out texture);
            imported.Add(key, found);
        }
        return found;
    }

    /// <summary>Turns the flats AddCityFlat queued into one batch per material under block.</summary>
    public static void BuildCityBlock(GameObject block)
    {
        if (pendingCity.Count == 0)
            return;
        // Atlas materials MaterialReader dropped since: their copies go too
        var dead = new List<Material>();
        foreach (var pair in cityMaterials)
            if (!pair.Key)
                dead.Add(pair.Key);
        foreach (Material key in dead)
        {
            Destroy(cityMaterials[key]);
            cityMaterials.Remove(key);
        }

        VitaFlatBatch owner = block.AddComponent<VitaFlatBatch>();
        var shadows = DaggerfallUnity.Settings.GeneralBillboardShadows ? UnityEngine.Rendering.ShadowCastingMode.TwoSided : UnityEngine.Rendering.ShadowCastingMode.Off;
        Vector3 normal = Vector3.Normalize(Vector3.up + Vector3.forward);
        foreach (var group in pendingCity)
        {
            Material material;
            if (!cityMaterials.TryGetValue(group.Key, out material) || !material)
            {
                material = new Material(Shader.Find(ShaderName));
                material.CopyPropertiesFromMaterial(group.Key);
                material.shaderKeywords = group.Key.shaderKeywords;
                material.renderQueue = group.Key.renderQueue;
                cityMaterials[group.Key] = material;
            }
            var go = new GameObject("VitaFlatBatch");
            go.transform.SetParent(block.transform, false);
            List<CityFlat> flats = group.Value;
            int n = flats.Count;
            var vertices = new Vector3[n * 4];
            var tangents = new Vector4[n * 4];
            var normals = new Vector3[n * 4];
            var batch = new Batch { uvs = new Vector2[n * 4] };
            var triangles = new int[n * 6];
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            float reach = 0, height = 0;
            for (int i = 0; i < n; i++)
            {
                CityFlat flat = flats[i];
                Vector3 pivot = go.transform.InverseTransformPoint(flat.pivot);
                min = Vector3.Min(min, pivot);
                max = Vector3.Max(max, pivot);
                float hx = flat.half.x, hy = flat.half.y;
                int v0 = i * 4;
                // MeshReader.GetBillboardMesh's corners, UVs and triangles
                tangents[v0] = new Vector4(hx, hy, 0, 0);
                tangents[v0 + 1] = new Vector4(-hx, hy, 0, 0);
                tangents[v0 + 2] = new Vector4(hx, -hy, 0, 0);
                tangents[v0 + 3] = new Vector4(-hx, -hy, 0, 0);
                Rect rect = flat.rect;
                batch.uvs[v0] = new Vector2(rect.x, rect.yMax);
                batch.uvs[v0 + 1] = new Vector2(rect.xMax, rect.yMax);
                batch.uvs[v0 + 2] = new Vector2(rect.x, rect.y);
                batch.uvs[v0 + 3] = new Vector2(rect.xMax, rect.y);
                for (int v = 0; v < 4; v++)
                {
                    vertices[v0 + v] = pivot;
                    normals[v0 + v] = normal;
                }
                triangles[i * 6] = v0;
                triangles[i * 6 + 1] = v0 + 1;
                triangles[i * 6 + 2] = v0 + 2;
                triangles[i * 6 + 3] = v0 + 3;
                triangles[i * 6 + 4] = v0 + 2;
                triangles[i * 6 + 5] = v0 + 1;
                reach = Mathf.Max(reach, Mathf.Abs(hx));
                height = Mathf.Max(height, Mathf.Abs(hy));
            }
            MakeMesh(go, batch, material, vertices, tangents, normals, triangles, min, max, reach, height, shadows, true);
            owner.batches.Add(batch);
        }
        pendingCity.Clear();
    }

    void LateUpdate()
    {
        // DaggerfallBillboard's shared facing: LookRotation of the camera's horizontal back vector (kept when looking straight up/down)
        if (!mainCamera)
        {
            GameObject go = GameObject.FindGameObjectWithTag("MainCamera");
            mainCamera = go ? go.GetComponent<Camera>() : null;
            if (!mainCamera)
                return;
        }
        Vector3 forward = mainCamera.transform.forward;
        Vector3 view = -new Vector3(forward.x, 0, forward.z);
        if (view != Vector3.zero)
            Shader.SetGlobalVector(facingId, view.normalized);

        if (!shown)
            return;
        // Animated flats: the billboard's timer still runs (its renderer is off, so it only counts frames); show its current frame
        foreach (Batch batch in batches)
        {
            bool changed = false;
            foreach (Item item in batch.animated)
            {
                if (!item.billboard)
                    continue;
                BillboardSummary summary = item.billboard.Summary;
                int frame = Mathf.Max(summary.CurrentFrame - 1, 0);     // AnimateBillboard draws CurrentFrame, then counts on
                if (frame == item.shownFrame)
                    continue;
                item.shownFrame = frame;
                Rect rect = summary.AtlasRects[summary.AtlasIndices[summary.Record].startIndex + frame];
                int v = item.firstVertex;
                batch.uvs[v] = new Vector2(rect.x, rect.yMax);
                batch.uvs[v + 1] = new Vector2(rect.xMax, rect.yMax);
                batch.uvs[v + 2] = new Vector2(rect.x, rect.y);
                batch.uvs[v + 3] = new Vector2(rect.xMax, rect.y);
                changed = true;
            }
            if (changed)
                batch.mesh.uv = batch.uvs;
        }
    }

    public void Show(bool on)
    {
        shown = on;
        foreach (Batch batch in batches)
        {
            batch.renderer.enabled = on;
            foreach (Item item in batch.items)
                if (item.renderer)
                    item.renderer.enabled = !on;
            foreach (Item item in batch.animated)
                item.shownFrame = -1;
        }
    }

    void OnDestroy()
    {
        foreach (Batch batch in batches)
            if (batch.mesh)
                Destroy(batch.mesh);
        foreach (Material material in materials)
            if (material)
                Destroy(material);
    }
}
#endif
