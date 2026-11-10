// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Dungeons and castles are draw bound on the render thread (~150 us per draw): inside the Palace 735 draws, and draws ≈ textures per
// visible model.
// So after a dungeon is laid out, the plain palette textures its models use are packed into one atlas, and each model's submeshes with
// those textures become one submesh with the atlas material (Resources/Vita/DaggerfallAtlasPalette.shader): one draw per model.
// Same look: each texture's own level 0 and mips (VitaPalette's) are copied into the atlas with a wrapped border, the shader wraps the
// model UVs into the texture's rect (uv2) and picks the same whole mip level, up to MaxLod (beyond it the separate textures blur
// further, here level MaxLod is used: only far, glancing surfaces).
// Left alone: animated, emissive (windows), normal/parallax-mapped and non-palette textures, sizes not a multiple of 2^MaxLod, models
// with one texture. Meshes are changed in place (same vertices, so the colliders stay valid); the atlas and its material belong to
// this component and go with the dungeon. settings.ini [Video] VitaAtlas = False turns it off.
// Cities (BuildCity, from StreamingWorld when VitaCityMerge is off): the same per block CombinedModels, cells keyed by default texture key.
// DaggerfallMesh.SetClimate asks OnClimate for those meshes: windows (day/night) are never in the atlas, so dusk only swaps the other
// submeshes, and a season change refills the cells with the new season's textures (same sizes) instead of dropping the atlas.
// In play (a town or hamlet streamed in with no loading screen) BuildCityStaged spreads the same work over frames (~4 ms each): it was one
// frame of 83-820 ms. The blocks draw their upstream submeshes until their atlas submesh is in, which looks the same.

#if UNITY_PSP2
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;

public class VitaAtlas : MonoBehaviour
{
    public const int MaxLod = 4;                // Daggerfall/Vita/AtlasPalette's VITA_ATLAS_MAX_LOD
    const int border = 1 << MaxLod;             // wrapped texels around each texture at level 0: one texel at level MaxLod
    const int maxDim = 2048;
    const float stageBudget = 0.004f;           // BuildCityStaged: seconds of atlas work per frame

    public static bool KeepUpstream;            // test switch: keep the upstream submeshes so Show can switch back

    Texture2D atlas;
    Material material;
    readonly List<Swap> swaps = new List<Swap>();

    // City atlas: cells by default texture key and the climate they hold; per block mesh, its upstream submeshes kept after the atlas one
    Dictionary<int, Source> cells;
    int dim;
    ClimateBases cellClimate;
    ClimateSeason cellSeason;
    static readonly Dictionary<DaggerfallMesh, CityMesh> cityMeshes = new Dictionary<DaggerfallMesh, CityMesh>();

    class CityMesh
    {
        public VitaAtlas atlas;
        public int[] rest;
    }

    class Swap
    {
        public MeshRenderer renderer;
        public Mesh mesh;
        public int[][] upstreamTriangles, atlasTriangles;
        public Material[] upstreamMaterials, atlasMaterials;
    }

    struct Source
    {
        public int archive, record, width, height;
        public int x, y;                        // cell position in the atlas (level 0, border included)
        public bool packed;
    }

    /// <summary>Packs the plain textures of root's models into an atlas and gives each model one draw for them.</summary>
    public static void Build(GameObject root)
    {
        DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
        if (!Enabled())
            return;
        float started = Time.realtimeSinceStartup;
        Dictionary<Material, CachedMaterial> cached = dfUnity.MaterialReader.VitaMaterialsByKey();
        Texture palette = null;

        // Models with at least two plain textures
        var renderers = new List<MeshRenderer>();
        var sources = new Dictionary<int, Source>();      // by texture key
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter ? filter.sharedMesh : null;
            Material[] materials = renderer.sharedMaterials;
            if (!mesh || !mesh.isReadable || mesh.subMeshCount != materials.Length || mesh.subMeshCount < 2 || !renderer.GetComponent<DaggerfallMesh>())
                continue;
            int plain = 0;
            foreach (Material m in materials)
            {
                if (IsPlain(m, cached, ref palette))
                {
                    plain++;
                    CachedMaterial cm = cached[m];
                    if (!sources.ContainsKey(cm.key))
                    {
                        int archive, record, frame;
                        MaterialReader.ReverseTextureKey(cm.key, out archive, out record, out frame);
                        int width, height;
                        VitaPalette.OriginalSize(m.mainTexture, out width, out height);
                        sources.Add(cm.key, new Source { archive = archive, record = record, width = width, height = height });
                    }
                }
            }
            if (plain >= 2)
                renderers.Add(renderer);
        }
        if (renderers.Count == 0)
            return;

        VitaAtlas owner = Create(root, sources, palette);
        if (!owner)
            return;
        int dim = owner.dim, packedCount = 0;
        foreach (Source source in sources.Values)
            if (source.packed)
                packedCount++;

        // Each model: its plain submeshes become one atlas submesh (first), the rest keep their own
        int drawsBefore = 0, drawsAfter = 0, conflicts = 0;
        var rects = new List<Vector4>();
        foreach (MeshRenderer renderer in renderers)
        {
            Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
            Material[] materials = renderer.sharedMaterials;
            rects.Clear();
            for (int i = 0; i < mesh.vertexCount; i++)
                rects.Add(Vector4.zero);
            var merged = new List<int>();
            var upstream = new int[mesh.subMeshCount][];
            var rest = new List<int>();
            bool conflict = false;
            for (int s = 0; s < mesh.subMeshCount && !conflict; s++)
            {
                upstream[s] = mesh.GetTriangles(s);
                Source source;
                if (!IsPlain(materials[s], cached, ref palette) || !sources.TryGetValue(cached[materials[s]].key, out source) || !source.packed)
                {
                    rest.Add(s);
                    continue;
                }
                var rect = new Vector4((float)(source.x + border) / dim, (float)(source.y + border) / dim, (float)source.width / dim, (float)source.height / dim);
                foreach (int v in upstream[s])
                {
                    if (rects[v] != Vector4.zero && rects[v] != rect)
                        conflict = true;    // a vertex shared by two textures: would need splitting; leave this model alone
                    rects[v] = rect;
                }
                merged.AddRange(upstream[s]);
            }
            int plainCount = mesh.subMeshCount - rest.Count;
            if (conflict || plainCount < 2)
            {
                conflicts += conflict ? 1 : 0;
                continue;
            }

            var swap = new Swap { renderer = renderer, mesh = mesh, upstreamTriangles = upstream, upstreamMaterials = materials };
            swap.atlasTriangles = new int[rest.Count + 1][];
            swap.atlasMaterials = new Material[rest.Count + 1];
            swap.atlasTriangles[0] = merged.ToArray();
            swap.atlasMaterials[0] = owner.material;
            for (int i = 0; i < rest.Count; i++)
            {
                swap.atlasTriangles[i + 1] = upstream[rest[i]];
                swap.atlasMaterials[i + 1] = materials[rest[i]];
            }
            mesh.SetUVs(1, rects);
            Apply(mesh, renderer, swap.atlasTriangles, swap.atlasMaterials);
            if (KeepUpstream)
                owner.swaps.Add(swap);
            drawsBefore += upstream.Length;
            drawsAfter += swap.atlasTriangles.Length;
        }
        Debug.LogFormat("VitaAtlas: {0} textures in {1}x{1} ({2} not packed), {3} models: submeshes {4} -> {5} ({6} shared-vertex models left), {7:F0} ms",
            packedCount, dim, sources.Count - packedCount, renderers.Count - conflicts, drawsBefore, drawsAfter, conflicts,
            (Time.realtimeSinceStartup - started) * 1000f);
    }

    static bool Enabled()
    {
        return DaggerfallUnity.Settings.VitaAtlas && !DaggerfallUnity.Instance.MeshReader.AddMeshLightmapUVs && QualitySettings.masterTextureLimit == 0;
    }

    /// <summary>
    /// For a city or town (StreamingWorld, VitaCityMerge off): packs the plain textures of its blocks' CombinedModels into one atlas and
    /// gives each block one draw for them, in place as Build does. Cells are keyed by default texture key so OnClimate can follow seasons.
    /// </summary>
    public static void BuildCity(GameObject location)
    {
        if (!Enabled())
            return;
        float started = Time.realtimeSinceStartup;
        Dictionary<Material, CachedMaterial> cached = DaggerfallUnity.Instance.MaterialReader.VitaMaterialsByKey();
        Texture palette = null;
        var sources = new Dictionary<int, Source>();      // by default texture key
        List<MeshRenderer> renderers = ScanCity(location, cached, ref palette, sources);
        if (renderers.Count == 0)
            return;
        VitaAtlas owner = Create(location, sources, palette);
        if (!owner)
            return;
        DaggerfallMesh climateOf = renderers[0].GetComponent<DaggerfallMesh>();
        owner.cells = sources;
        owner.cellClimate = climateOf.Climate;
        owner.cellSeason = climateOf.Season;

        int drawsBefore = 0, drawsAfter = 0, conflicts = 0;
        var rects = new List<Vector4>();
        foreach (MeshRenderer renderer in renderers)
            owner.ApplyCity(renderer, cached, ref palette, rects, ref drawsBefore, ref drawsAfter, ref conflicts);
        Debug.LogFormat("VitaAtlas city: {0} textures in {1}x{1}, {2} blocks: submeshes {3} -> {4} ({5} shared-vertex blocks left), {6:F0} ms",
            sources.Count, owner.dim, renderers.Count - conflicts, drawsBefore, drawsAfter, conflicts, (Time.realtimeSinceStartup - started) * 1000f);
        VitaMemory.Step("atlas city applied");
    }

    /// <summary>
    /// BuildCity for a location streamed in during play (StreamingWorld with allowYield, no loading screen): the same atlas, built over
    /// frames with about stageBudget of work in each. Run it on an object that outlives the location (DaggerfallUnity): the native buffer
    /// is only freed when this finishes. Gives up (nothing changed) if the location goes or its climate/season changes meanwhile.
    /// </summary>
    public static IEnumerator BuildCityStaged(GameObject location)
    {
        if (!Enabled() || !location)
            yield break;
        float started = Time.realtimeSinceStartup, frameStart = started, work = 0f;
        int frames = 1;
        Dictionary<Material, CachedMaterial> cached = DaggerfallUnity.Instance.MaterialReader.VitaMaterialsByKey();
        Texture palette = null;
        var sources = new Dictionary<int, Source>();
        List<MeshRenderer> renderers = ScanCity(location, cached, ref palette, sources);
        var keys = new List<int>(sources.Keys);
        int dim = renderers.Count == 0 ? 0 : PackAll(keys, sources);
        if (dim == 0)
            yield break;
        DaggerfallMesh climateOf = renderers[0].GetComponent<DaggerfallMesh>();
        ClimateBases climate = climateOf.Climate;
        ClimateSeason season = climateOf.Season;

        // Fill a few cells a frame (each is a pack read from the card), then upload
        Texture2D texture = null;
        int size = MipChainSize(dim);
        System.IntPtr data = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            ClearBuffer(data, size);
            int[] levelOffsets = LevelOffsets(dim);
            var levels = new System.IntPtr[MaxLod + 1];
            TextureFile textureFile = null;
            List<int> order = ByArchive(keys, sources);
            for (int i = 0; i < order.Count; i++)
            {
                if (i > 0 && sources[order[i]].archive != sources[order[i - 1]].archive)
                    textureFile = null;
                Source source = sources[order[i]];
                FillSource(ref source, dim, data, levelOffsets, levels, ref textureFile);
                sources[order[i]] = source;
                if (Time.realtimeSinceStartup - frameStart > stageBudget)
                {
                    work += Time.realtimeSinceStartup - frameStart;
                    yield return null;
                    frames++;
                    frameStart = Time.realtimeSinceStartup;
                    if (!location)
                        yield break;
                }
            }
            texture = VitaPalette.LoadRaw(data, size, dim, dim, true, false);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(data);
        }
        if (!texture)
            yield break;
        work += Time.realtimeSinceStartup - frameStart;
        yield return null;
        frames++;
        frameStart = Time.realtimeSinceStartup;
        if (!location || !climateOf || climateOf.Climate != climate || climateOf.Season != season)
        {
            Destroy(texture);
            yield break;
        }
        VitaAtlas owner = Own(location, dim, texture, palette);
        owner.cells = sources;
        owner.cellClimate = climate;
        owner.cellSeason = season;

        // Blocks a few at a time; each one is whole (atlas submesh + the rest) after its frame
        int drawsBefore = 0, drawsAfter = 0, conflicts = 0;
        var rects = new List<Vector4>();
        cached = DaggerfallUnity.Instance.MaterialReader.VitaMaterialsByKey();   // a new dictionary each call: once a frame
        foreach (MeshRenderer renderer in renderers)
        {
            if (!owner)
                yield break;
            if (renderer)
                owner.ApplyCity(renderer, cached, ref palette, rects, ref drawsBefore, ref drawsAfter, ref conflicts);
            if (Time.realtimeSinceStartup - frameStart > stageBudget)
            {
                work += Time.realtimeSinceStartup - frameStart;
                yield return null;
                frames++;
                frameStart = Time.realtimeSinceStartup;
                cached = DaggerfallUnity.Instance.MaterialReader.VitaMaterialsByKey();
            }
        }
        work += Time.realtimeSinceStartup - frameStart;
        Debug.LogFormat("VitaAtlas city (in play): {0} textures in {1}x{1}, {2} blocks: submeshes {3} -> {4} ({5} shared-vertex blocks left), {6:F0} ms work over {7} frames, {8:F0} ms",
            sources.Count, dim, renderers.Count - conflicts, drawsBefore, drawsAfter, conflicts, work * 1000f, frames, (Time.realtimeSinceStartup - started) * 1000f);
    }

    // The city's blocks with at least two plain textures, and their textures by default key into sources
    static List<MeshRenderer> ScanCity(GameObject location, Dictionary<Material, CachedMaterial> cached, ref Texture palette, Dictionary<int, Source> sources)
    {
        var renderers = new List<MeshRenderer>();
        foreach (MeshRenderer renderer in location.GetComponentsInChildren<MeshRenderer>())
        {
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter ? filter.sharedMesh : null;
            DaggerfallMesh dfMesh = renderer.GetComponent<DaggerfallMesh>();
            if ((!renderer.enabled && !VitaCulling.IsPreCulled(renderer)) || !mesh || !mesh.isReadable || !dfMesh)
                continue;
            // City gates swap their mesh at dusk and dawn: the other model has no atlas submesh or rects, so its wall vanished at night
            if (renderer.GetComponent<DaggerfallCityGate>())
                continue;
            int[] keys = dfMesh.GetDefaultTextures();
            Material[] materials = renderer.sharedMaterials;
            if (keys.Length < 2 || keys.Length != mesh.subMeshCount || materials.Length != keys.Length)
                continue;
            int plain = 0;
            for (int s = 0; s < keys.Length; s++)
            {
                if (!IsPlain(materials[s], cached, ref palette))
                    continue;
                plain++;
                if (!sources.ContainsKey(keys[s]))
                    sources.Add(keys[s], SourceOf(materials[s], cached[materials[s]].key));
            }
            if (plain >= 2)
                renderers.Add(renderer);
        }
        return renderers;
    }

    // One block: its plain submeshes become one atlas submesh (first), the rest keep their own
    void ApplyCity(MeshRenderer renderer, Dictionary<Material, CachedMaterial> cached, ref Texture palette, List<Vector4> rects,
        ref int drawsBefore, ref int drawsAfter, ref int conflicts)
    {
        Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
        DaggerfallMesh dfMesh = renderer.GetComponent<DaggerfallMesh>();
        int[] keys = dfMesh.GetDefaultTextures();
        Material[] materials = renderer.sharedMaterials;
        if (!mesh || materials.Length != mesh.subMeshCount || keys.Length != mesh.subMeshCount)
            return;
        rects.Clear();
        for (int i = 0; i < mesh.vertexCount; i++)
            rects.Add(Vector4.zero);
        var merged = new List<int>();
        var upstream = new int[mesh.subMeshCount][];
        var rest = new List<int>();
        bool conflict = false;
        for (int s = 0; s < mesh.subMeshCount && !conflict; s++)
        {
            upstream[s] = mesh.GetTriangles(s);
            Vector4 rect;
            if (!IsPlain(materials[s], cached, ref palette) || !TryGetRect(keys[s], out rect))
            {
                rest.Add(s);
                continue;
            }
            foreach (int v in upstream[s])
            {
                if (rects[v] != Vector4.zero && rects[v] != rect)
                    conflict = true;    // a vertex shared by two textures: would need splitting; leave this block alone
                rects[v] = rect;
            }
            merged.AddRange(upstream[s]);
        }
        if (conflict || mesh.subMeshCount - rest.Count < 2)
        {
            conflicts += conflict ? 1 : 0;
            return;
        }
        var triangles = new int[rest.Count + 1][];
        var atlasMaterials = new Material[rest.Count + 1];
        triangles[0] = merged.ToArray();
        atlasMaterials[0] = material;
        for (int i = 0; i < rest.Count; i++)
        {
            triangles[i + 1] = upstream[rest[i]];
            atlasMaterials[i + 1] = materials[rest[i]];
        }
        mesh.SetUVs(1, rects);
        Apply(mesh, renderer, triangles, atlasMaterials);
        cityMeshes[dfMesh] = new CityMesh { atlas = this, rest = rest.ToArray() };
        drawsBefore += upstream.Length;
        drawsAfter += triangles.Length;
    }

    // The atlas rect (x, y, width, height in atlas UV) of a default texture key, for the shader's uv2
    bool TryGetRect(int key, out Vector4 rect)
    {
        Source source;
        if (cells == null || !cells.TryGetValue(key, out source) || !source.packed)
        {
            rect = Vector4.zero;
            return false;
        }
        rect = new Vector4((float)(source.x + border) / dim, (float)(source.y + border) / dim, (float)source.width / dim, (float)source.height / dim);
        return true;
    }

    /// <summary>
    /// From DaggerfallMesh.SetClimate: for a city block with an atlas, the new materials (one per upstream submesh) in the atlas layout:
    /// the atlas first, then the submeshes kept separate. A season change refills the atlas cells first. Other meshes: materials as given.
    /// </summary>
    public static Material[] OnClimate(DaggerfallMesh mesh, Material[] materials, ClimateBases climate, ClimateSeason season)
    {
        CityMesh city;
        if (!cityMeshes.TryGetValue(mesh, out city) || !city.atlas || !city.atlas.material)
            return materials;
        if (climate != city.atlas.cellClimate || season != city.atlas.cellSeason)
            city.atlas.Refill(climate, season);
        var result = new Material[city.rest.Length + 1];
        result[0] = city.atlas.material;
        for (int i = 0; i < city.rest.Length; i++)
            result[i + 1] = materials[city.rest[i]];
        return result;
    }

    // Season/climate change while in town: each cell takes its key's new texture if that is plain and the same size (else it keeps the
    // old one), and the atlas is rebuilt once. A load-sized hitch at a season change, never in normal play.
    void Refill(ClimateBases climate, ClimateSeason season)
    {
        float started = Time.realtimeSinceStartup;
        cellClimate = climate;
        cellSeason = season;
        DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
        Dictionary<Material, CachedMaterial> cached = dfUnity.MaterialReader.VitaMaterialsByKey();
        Texture palette = material.GetTexture(VitaPalette.PaletteTex);
        var keys = new List<int>(cells.Keys);
        int changed = 0;
        foreach (int key in keys)
        {
            Source source = cells[key];
            Material m = dfUnity.MaterialReader.ChangeClimate(key, climate, season, WindowStyle.Day);
            if (!source.packed || !IsPlain(m, cached, ref palette))
                continue;
            Source next = SourceOf(m, cached[m].key);
            if (next.width != source.width || next.height != source.height || (next.archive == source.archive && next.record == source.record))
                continue;
            source.archive = next.archive;
            source.record = next.record;
            cells[key] = source;
            changed++;
        }
        if (changed == 0)
            return;
        Texture2D refilled = FillTexture(keys, cells, dim);
        if (!refilled)
            return;
        refilled.wrapMode = TextureWrapMode.Clamp;
        material.mainTexture = refilled;
        Destroy(atlas);
        atlas = refilled;
        Debug.LogFormat("VitaAtlas city: {0} cells refilled for {1} {2}, {3:F0} ms", changed, climate, season, (Time.realtimeSinceStartup - started) * 1000f);
    }

    static Source SourceOf(Material m, int textureKey)
    {
        int archive, record, frame, width, height;
        MaterialReader.ReverseTextureKey(textureKey, out archive, out record, out frame);
        VitaPalette.OriginalSize(m.mainTexture, out width, out height);
        return new Source { archive = archive, record = record, width = width, height = height };
    }

    // Shelf packing, tallest first, into the smallest square that holds everything (what doesn't fit at maxDim stays separate), then the
    // atlas texture and its material, owned by a new VitaAtlas on host. Null if fewer than 2 textures fit.
    static VitaAtlas Create(GameObject host, Dictionary<int, Source> sources, Texture palette)
    {
        var keys = new List<int>(sources.Keys);
        int dim = PackAll(keys, sources);
        if (dim == 0)
            return null;
        VitaMemory.LoadCollect();   // free the load's garbage before the atlas's 1.4-5.6 MB
        VitaMemory.Step("atlas " + dim + " packed");
        Texture2D texture = FillTexture(keys, sources, dim);
        VitaMemory.Step("atlas uploaded");
        return texture ? Own(host, dim, texture, palette) : null;
    }

    // Packs keys (sorted tallest first) at the smallest dim that holds them all, up to maxDim; the dim, or 0 if fewer than 2 fit
    static int PackAll(List<int> keys, Dictionary<int, Source> sources)
    {
        keys.Sort((a, b) => sources[b].height != sources[a].height ? sources[b].height - sources[a].height : a - b);
        int dim = 256;
        while (!Pack(keys, sources, dim) && dim < maxDim)
            dim *= 2;
        int packedCount = 0;
        foreach (int key in keys)
            if (sources[key].packed)
                packedCount++;
        return packedCount < 2 ? 0 : dim;
    }

    // A new VitaAtlas on host owning texture and its material
    static VitaAtlas Own(GameObject host, int dim, Texture2D texture, Texture palette)
    {
        VitaAtlas owner = host.AddComponent<VitaAtlas>();
        owner.dim = dim;
        owner.atlas = texture;
        owner.atlas.wrapMode = TextureWrapMode.Clamp;
        owner.material = new Material(Shader.Find(VitaPalette.AtlasShaderName));
        owner.material.name = "VitaAtlas";
        owner.material.mainTexture = owner.atlas;
        owner.material.SetTexture(VitaPalette.PaletteTex, palette);
        return owner;
    }

    // The atlas texture: Fill into native memory, upload, free. Everything here stays off the managed heap: the managed atlas buffer and
    // per-texture level arrays grew the never-shrinking Boehm heap 106 -> 131 MB in one city atlas.
    static Texture2D FillTexture(List<int> keys, Dictionary<int, Source> sources, int dim)
    {
        int size = MipChainSize(dim);
        System.IntPtr data = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            Fill(keys, sources, dim, data, size);
            return VitaPalette.LoadRaw(data, size, dim, dim, true, false);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(data);
        }
    }

    static int MipChainSize(int dim)
    {
        int size = 0;
        for (int w = dim; w >= 1; w /= 2)
            size += w * w;
        return size;
    }

    static int[] LevelOffsets(int dim)
    {
        var levelOffsets = new int[MaxLod + 1];
        for (int level = 1; level <= MaxLod; level++)
            levelOffsets[level] = levelOffsets[level - 1] + (dim >> (level - 1)) * (dim >> (level - 1));
        return levelOffsets;
    }

    static unsafe void ClearBuffer(System.IntPtr data, int size)
    {
        byte* atlas = (byte*)data;
        for (int i = 0; i < size; i++)
            atlas[i] = 0;
    }

    // The packed keys grouped by archive (first-seen order), so a fallback TEXTURE file is opened once per archive
    static List<int> ByArchive(List<int> keys, Dictionary<int, Source> sources)
    {
        var byArchive = new Dictionary<int, List<int>>();
        var archives = new List<int>();
        foreach (int key in keys)
        {
            if (!sources[key].packed)
                continue;
            List<int> list;
            if (!byArchive.TryGetValue(sources[key].archive, out list))
            {
                byArchive.Add(sources[key].archive, list = new List<int>());
                archives.Add(sources[key].archive);
            }
            list.Add(key);
        }
        var order = new List<int>();
        foreach (int archive in archives)
            order.AddRange(byArchive[archive]);
        return order;
    }

    // Level 0 and mips 1..MaxLod of each packed texture, wrapped into its cell (levels past MaxLod are never sampled), into data (a full
    // mip chain of dim, size bytes)
    static void Fill(List<int> keys, Dictionary<int, Source> sources, int dim, System.IntPtr data, int size)
    {
        ClearBuffer(data, size);
        int[] levelOffsets = LevelOffsets(dim);
        var levels = new System.IntPtr[MaxLod + 1];
        TextureFile textureFile = null;
        List<int> order = ByArchive(keys, sources);
        for (int i = 0; i < order.Count; i++)
        {
            if (i > 0 && sources[order[i]].archive != sources[order[i - 1]].archive)
                textureFile = null;
            Source source = sources[order[i]];
            FillSource(ref source, dim, data, levelOffsets, levels, ref textureFile);
            sources[order[i]] = source;
        }
    }

    // One packed texture's levels into its cell: from the baked world textures (VitaWorldTextures), read in place from the record (no
    // TEXTURE read, no mips built here, no managed copy), else from the TEXTURE file (textureFile: this archive's, opened on first need).
    // A replaced or resized texture is left unpacked.
    static unsafe void FillSource(ref Source source, int dim, System.IntPtr data, int[] levelOffsets, System.IntPtr[] levels, ref TextureFile textureFile)
    {
        byte* atlas = (byte*)data;
        System.IntPtr memory;
        if (VitaWorldTextures.ReadLevels(source.archive, source.record, source.width, source.height, MaxLod, out memory, levels))
        {
            try
            {
                for (int l = 0, w = source.width, h = source.height; l <= MaxLod; l++, w /= 2, h /= 2)
                    CopyWrapped((byte*)levels[l], w, h, border >> l, atlas, levelOffsets[l] + ((source.y >> l) + (border >> l)) * (dim >> l) +
                        (source.x >> l) + (border >> l), dim >> l);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(memory);
            }
            return;
        }
        if (textureFile == null)
            textureFile = new TextureFile(Path.Combine(DaggerfallUnity.Instance.Arena2Path, TextureFile.IndexToFileName(source.archive)), FileUsage.UseMemory, true);
        DFSize size0;
        byte[] level = VitaPalette.Frame(textureFile.GetDFBitmap(source.record, 0), 0, 0, out size0);
        if (size0.Width != source.width || size0.Height != source.height)
        {
            source.packed = false;      // replaced or resized texture: keep it separate
            return;
        }
        for (int l = 0, w = source.width, h = source.height; l <= MaxLod; l++)
        {
            fixed (byte* src = level)
                CopyWrapped(src, w, h, border >> l, atlas, levelOffsets[l] + ((source.y >> l) + (border >> l)) * (dim >> l) +
                    (source.x >> l) + (border >> l), dim >> l);
            if (l < MaxLod)
            {
                level = VitaPalette.HalfSize(level, w, h, textureFile.Palette);
                w /= 2;
                h /= 2;
            }
        }
    }

    // One level of a texture into its cell with a wrapped border of b texels (b <= w, b <= h: sizes are multiples of 2^MaxLod), the texture's
    // top-left at data + origin. Rows as block copies: was a per-texel loop with two % each (no hardware divide), the atlas's main load cost.
    public static unsafe void CopyWrapped(byte* level, int w, int h, int b, byte* data, int origin, int stride)
    {
        for (int y = -b; y < h + b; y++)
        {
            byte* row = data + origin + y * stride, src = level + (y < 0 ? y + h : y >= h ? y - h : y) * w;
            System.Buffer.MemoryCopy(src + w - b, row - b, b, b);
            System.Buffer.MemoryCopy(src, row, w, w);
            System.Buffer.MemoryCopy(src, row + w, b, b);
        }
    }

    public void Show(bool on)
    {
        foreach (Swap swap in swaps)
            if (swap.renderer && swap.mesh)
                Apply(swap.mesh, swap.renderer, on ? swap.atlasTriangles : swap.upstreamTriangles, on ? swap.atlasMaterials : swap.upstreamMaterials);
    }

    static void Apply(Mesh mesh, MeshRenderer renderer, int[][] triangles, Material[] materials)
    {
        mesh.subMeshCount = triangles.Length;
        for (int s = 0; s < triangles.Length; s++)
            mesh.SetTriangles(triangles[s], s, false);
        renderer.sharedMaterials = materials;
    }

    // A cached, unanimated, opaque palette texture with no extra maps, sized for the atlas's mips; all with one palette texture
    static bool IsPlain(Material m, Dictionary<Material, CachedMaterial> cached, ref Texture palette)
    {
        CachedMaterial cm;
        if (!m || m.shader.name != VitaPalette.ShaderName || m.shaderKeywords.Length != 0 || m.color != Color.white ||
            !cached.TryGetValue(m, out cm) || cm.singleFrameCount > 1 || cm.isWindow)
            return false;
        Texture texture = m.mainTexture;
        Texture paletteTexture = m.GetTexture(VitaPalette.PaletteTex);
        int width = 0, height = 0;
        if (texture)
            VitaPalette.OriginalSize(texture, out width, out height);
        if (!texture || width % border != 0 || height % border != 0 || width > 512 || height > 512 ||
            !paletteTexture || (palette && palette != paletteTexture))
            return false;
        int archive, record, frame;
        MaterialReader.ReverseTextureKey(cm.key, out archive, out record, out frame);
        if (frame != 0)
            return false;
        palette = paletteTexture;
        return true;
    }

    // Shelf packing in key order; cells are texture + border on each side, so every cell starts on a multiple of 2^MaxLod
    static bool Pack(List<int> keys, Dictionary<int, Source> sources, int dim)
    {
        int x = 0, y = 0, shelf = 0;
        bool all = true;
        foreach (int key in keys)
        {
            Source source = sources[key];
            int w = source.width + border * 2, h = source.height + border * 2;
            if (x + w > dim)
            {
                x = 0;
                y += shelf;
                shelf = 0;
            }
            source.packed = x + w <= dim && y + h <= dim;
            if (source.packed)
            {
                source.x = x;
                source.y = y;
                x += w;
                shelf = Mathf.Max(shelf, h);
            }
            else
                all = false;
            sources[key] = source;
        }
        return all;
    }

    void OnDestroy()
    {
        if (cells != null)
        {
            var gone = new List<DaggerfallMesh>();
            foreach (var pair in cityMeshes)
                if (pair.Value.atlas == this || !pair.Key)
                    gone.Add(pair.Key);
            foreach (DaggerfallMesh mesh in gone)
                cityMeshes.Remove(mesh);
        }
        if (atlas)
            Destroy(atlas);
        if (material)
            Destroy(material);
    }
}
#endif
