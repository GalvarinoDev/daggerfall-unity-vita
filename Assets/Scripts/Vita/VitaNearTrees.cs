// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Low Poly Trees (SquidKamer's mod, converted by vita/tools/lowpolytrees.py into Resources/VitaTrees) near the player, setting
// VitaLowPolyTrees. PC DFU turns every wilderness tree of the 3x3 tiles around the player into a Unity terrain tree (MeshReplacement.
// ImportNatureGameObject): thousands per tile, far too many for the Vita. Here only the 4x4 cells of 8x8 tiles (~51 m) around the
// player get models (CellTiles, Ring), merged per cell into one mesh (one submesh per atlas); everywhere else the billboards stay.
// TerrainNature puts the trees that have a model into a second billboard batch whose material has VITA_NEAR_TREES: its billboards
// collapse inside _VitaNearTreeRect, the area the models cover. A new 3x3 block is built a few trees per frame, kept hidden, and
// swapped in (rect and cells together) once complete, so trees never vanish or show twice.
// Scale, tint and rotation are drawn as ImportNatureGameObject draws them (same random state), so trees match PC DFU's.

#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using DaggerfallWorkshop.Game;

namespace DaggerfallWorkshop
{
    public class VitaNearTrees : MonoBehaviour
    {
        const int tileDim = 128;            // MapsFile.WorldMapTileDim
        const int bucketTiles = 4;          // trees are kept in 4x4-tile buckets (32x32 per terrain); cells gather them
        const int bucketsPerSide = tileDim / bucketTiles;
        const int treesPerFrame = 24;       // build budget
        const int maxVertices = 65000;      // 16-bit indices
        const string keyword = "VITA_NEAR_TREES";
        static readonly int rectId = Shader.PropertyToID("_VitaNearTreeRect");
        static readonly Vector4 noRect = new Vector4(1e9f, 1e9f, -1e9f, -1e9f);

        class Model { public Vector3[] vertices, normals; public Vector2[] uvs; public int[][] triangles; }
        class TreeModel { public int model; public float scale; public int[] atlas; public bool[] cutout; public Color32[] colour; }
        class Atlas { public Material material; public Texture2D index, palette; public int refs; public bool rgba; public float releasedAt = -1; }
        internal struct Instance { public int record; public Vector3 position; public float scale, rotation, tint; public int archive; public VitaLocationTrees owner; }   // archive 0: the tile's
        class Cell { public readonly List<GameObject> objects = new List<GameObject>(); public readonly List<int> atlases = new List<int>(); }
        class Tile
        {
            public int archive;
            public int mapPixelX, mapPixelY;   // the map pixel laid out; a reused terrain shows its new ground a frame or more before its nature
            public readonly List<Instance>[] buckets = new List<Instance>[bucketsPerSide * bucketsPerSide];
            public readonly Cell[] cells = new Cell[bucketsPerSide * bucketsPerSide];
        }
        struct CellKey : System.IEquatable<CellKey>
        {
            public DaggerfallTerrain terrain;
            public int index;
            public bool Equals(CellKey other) { return terrain == other.terrain && index == other.index; }
        }

        static Model[] models;
        static readonly Dictionary<int, TreeModel> treeModels = new Dictionary<int, TreeModel>();    // archive << 8 | record
        static readonly Dictionary<int, Atlas> atlases = new Dictionary<int, Atlas>();               // atlas << 1 | cutout
        static readonly Dictionary<DaggerfallTerrain, Tile> tiles = new Dictionary<DaggerfallTerrain, Tile>();
        static bool loaded, loadFailed;
        /// <summary>Models hidden, every billboard shown and nothing built; cells kept (test switch)</summary>
        public static bool Paused;
        bool pausedShown;
        /// <summary>Cells around the player's cell that get models (1 = 3x3, 0 = only the player's cell) and leaves left out (cost tests);
        /// call Rebuild() after changing them</summary>
        public static int Ring = 2;
        /// <summary>Even block: 2 x Ring cells a side around the cell corner nearest the player instead of 2 x Ring + 1 around the player's cell.
        /// 3x3 swapped at 51-77 m (easy to see), 5x5 at 102-128 m cost 8-12 FPS in woods, 4x4 swaps at 77-102 m (0-3 FPS). Default 5x5.</summary>
        public static bool Even = false;
        public static bool NoLeaves;
        /// <summary>Log each block swap. Off in play: a Debug.Log line costs 7-14 ms on hardware and this one came
        /// every ~51 m of walking, inside the swap frame (every walk hitch over 100 ms came 0.1-0.2 s after a swap line)</summary>
        public static bool LogSwaps;
        /// <summary>Cell size in tiles (4, 8 or 16: ~26, ~51 or ~102 m); call Rebuild() after changing it</summary>
        public static int CellTiles = 8;
        /// <summary>Leaves from a full-colour copy of their atlas (TreeLeavesRGBA: one plain read instead of the palette lookup). tree3: woods
        /// 30-37 -> 39-48 FPS against palette leaves, at +4 MB video memory per atlas (5.6 vs 1.4 MB)</summary>
        public static bool RgbaLeaves = true;
        /// <summary>RGBA leaves lit by abs(N.L) (tree3's look-alike, both faces bright) instead of PC's SpeedTree Lambert, max(N.L, 0) with
        /// the same normal on both faces</summary>
        public static bool AbsLeafLight { set { Shader.SetGlobalFloat(leafAbsId, value ? 1 : 0); } }
        static readonly int leafAbsId = Shader.PropertyToID("_VitaLeafAbs");
        const int rgbaKey = 1 << 20;
        const long RgbaMinFreeMain = 40L << 20;     // below it a new RGBA leaf atlas is made as a palette one
        static int CellsPerSide { get { return tileDim / CellTiles; } }
        readonly List<Instance> cellTrees = new List<Instance>();
        /// <summary>The block around the player's current cell is shown</summary>
        public static bool Ready { get { return instance && instance.ready; } }
        bool ready;

        public static void Rebuild()
        {
            if (instance)
                instance.Forget();
        }
        static VitaNearTrees instance;

        // Shown block (rect follows its centre terrain, which moves with the floating origin) and the block being built
        readonly List<CellKey> shown = new List<CellKey>(), wanted = new List<CellKey>();
        DaggerfallTerrain shownTerrain;
        int shownCell = -1;
        // Incremental builder (one cell at a time)
        CellKey building;
        bool isBuilding;
        int buildNext;
        readonly List<Vector3> vertices = new List<Vector3>(), normals = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<Color32> colours = new List<Color32>();
        readonly Dictionary<int, List<int>> triangles = new Dictionary<int, List<int>>();
        Cell buildCell;

        /// <summary>From TerrainNature.LayoutNature after the batch is cleared: the batch for trees that have a model (cleared), or null when
        /// the setting is off or no tree of this archive has one.</summary>
        public static DaggerfallBillboardBatch BeginLayout(DaggerfallTerrain terrain, DaggerfallBillboardBatch batch)
        {
            ReleaseTile(terrain);
            // The tree batch is the nature batch's only child batch. Not found by name: SetMaterial renames it ("DaggerfallBillboardBatch [505]"),
            // so each reuse of a terrain made one more, and the old ones kept the trees of the tile it had been: trees floating over the
            // new ground (tree3/tree4, found by float2)
            DaggerfallBillboardBatch treeBatch = null;
            foreach (Transform child in batch.transform)
                if (treeBatch = child.GetComponent<DaggerfallBillboardBatch>())
                    break;
            if (treeBatch)
                treeBatch.Clear();
            if (!DaggerfallUnity.Settings.VitaLowPolyTrees || !Load() || !HasModels(VitaBiomes.ModelArchive(batch.TextureArchive)))
            {
                if (treeBatch)
                    treeBatch.Apply();
                return null;
            }
            if (!treeBatch)
            {
                var go = new GameObject("VitaNearTrees");
                go.transform.SetParent(batch.transform, false);
                go.layer = batch.gameObject.layer;
                treeBatch = go.AddComponent<DaggerfallBillboardBatch>();
            }
            treeBatch.SetMaterial(VitaBiomes.ModelArchive(batch.TextureArchive));
            Material material = treeBatch.GetComponent<MeshRenderer>().sharedMaterial;
            if (material && !material.IsKeywordEnabled(keyword))
                material.EnableKeyword(keyword);
            var tile = new Tile { archive = VitaBiomes.ModelArchive(batch.TextureArchive), mapPixelX = terrain.MapPixelX, mapPixelY = terrain.MapPixelY };
            tiles[terrain] = tile;
            if (!instance)
                instance = new GameObject("VitaNearTrees").AddComponent<VitaNearTrees>();
            return treeBatch;
        }

        /// <summary>From TerrainNature.LayoutNature for each tree: true if record has a model (then it goes into the BeginLayout batch).
        /// Draws scale, tint and rotation as MeshReplacement.ImportNatureGameObject does, leaving the random state as it was.</summary>
        public static bool Add(DaggerfallTerrain terrain, Terrain unityTerrain, int record, int x, int y)
        {
            Tile tile;
            if (!tiles.TryGetValue(terrain, out tile) || !treeModels.ContainsKey(tile.archive << 8 | record))
                return false;
            const float tilemapDim = tileDim - 1;
            Random.State state = Random.state;
            var tree = new Instance { record = record };
            tree.scale = Random.Range(0.6f, 1.4f);
            tree.tint = Random.value;                  // drawn to keep the sequence; PC's SpeedTree shaders ignore the colour
            tree.rotation = Random.Range(0f, 360f);    // TreeInstance.rotation is in radians: DFU passes degrees, so this is just random
            Random.state = state;
            TerrainData data = unityTerrain.terrainData;
            tree.position = new Vector3(x / tilemapDim * data.size.x, data.GetInterpolatedHeight(x / tilemapDim, y / tilemapDim), y / tilemapDim * data.size.z);
            int bucket = Mathf.Min(y / bucketTiles, bucketsPerSide - 1) * bucketsPerSide + Mathf.Min(x / bucketTiles, bucketsPerSide - 1);
            if (tile.buckets[bucket] == null)
                tile.buckets[bucket] = new List<Instance>();
            tile.buckets[bucket].Add(tree);
            return true;
        }

        /// <summary>Setting switched off: models gone, every billboard back.</summary>
        public static void ClearAll()
        {
            var terrains = new List<DaggerfallTerrain>(tiles.Keys);
            foreach (DaggerfallTerrain terrain in terrains)
                ReleaseTile(terrain);
            Shader.SetGlobalVector(rectId, noRect);
            if (instance)
                instance.Forget();
        }

        // Every cell freed and the block rebuilt from scratch (a shown terrain was re-laid out or destroyed)
        void Forget()
        {
            foreach (var pair in tiles)
                for (int i = 0; i < pair.Value.cells.Length; i++)
                    ReleaseCell(pair.Key, i);
            shown.Clear();
            wanted.Clear();
            shownTerrain = null;
            shownCell = -1;
            isBuilding = false;
        }

        void OnDestroy()
        {
            ClearAll();
        }

        void LateUpdate()
        {
            DropReleasedAtlases();
            if (tiles.Count == 0)
                return;
            DropDestroyedTiles();
            UpdateLocationTrees();
            if (Paused != pausedShown)
            {
                pausedShown = Paused;
                foreach (CellKey key in shown)
                    foreach (GameObject go in tiles[key.terrain].cells[key.index].objects)
                        go.SetActive(!Paused);
            }
            if (Paused)
            {
                ready = false;
                SetRect();
                return;
            }
            GameObject player = GameManager.Instance.PlayerObject;
            DaggerfallTerrain terrain;
            int cell;
            // Even block: the cell whose lower corner is the corner nearest the player anchors it (half a cell up in x and z)
            float half = Even ? DaggerfallConnect.Arena2.MapsFile.WorldMapTerrainDim * MeshReader.GlobalScale / CellsPerSide * 0.5f : 0;
            if (!player || GameManager.Instance.IsPlayerInside || !FindCell(player.transform.position + new Vector3(half, 0, half), out terrain, out cell))
            {
                ready = false;
                SetRect();
                return;
            }
            if (terrain != shownTerrain || cell != shownCell)
            {
                Want(terrain, cell);
                if (BuildStep())
                    Swap(terrain, cell);
            }
            ready = terrain == shownTerrain && cell == shownCell;
            SetRect();
        }

        // Last cell offset of the block: Ring, or Ring - 1 for an even block
        static int Hi { get { return Even ? Ring - 1 : Ring; } }

        // The 3x3 cells around (terrain, cell), across terrain edges where a neighbour is loaded
        void Want(DaggerfallTerrain terrain, int cell)
        {
            wanted.Clear();
            Vector3 size = terrain.GetComponent<Terrain>().terrainData.size;
            float cellSize = size.x / CellsPerSide;
            int cx = cell % CellsPerSide, cy = cell / CellsPerSide;
            for (int dy = -Ring; dy <= Hi; dy++)
            {
                for (int dx = -Ring; dx <= Hi; dx++)
                {
                    Vector3 centre = terrain.transform.position + new Vector3((cx + dx + 0.5f) * cellSize, 0, (cy + dy + 0.5f) * cellSize);
                    DaggerfallTerrain t;
                    int c;
                    if (FindCell(centre, out t, out c))
                        wanted.Add(new CellKey { terrain = t, index = c });
                }
            }
        }

        // Builds the wanted cells a few trees per frame; true once all exist
        bool BuildStep()
        {
            int budget = treesPerFrame;
            foreach (CellKey key in wanted)
            {
                Tile tile = tiles[key.terrain];
                if (tile.cells[key.index] != null)
                    continue;
                if (!isBuilding || building.terrain != key.terrain || building.index != key.index)
                    StartCell(key);
                int count = cellTrees.Count;
                while (buildNext < count && budget-- > 0)
                    AddTree(tile, cellTrees[buildNext++]);
                if (buildNext < count)
                    return false;
                FinishCell(tile);
                return false;   // next cell next frame
            }
            return true;
        }

        void StartCell(CellKey key)
        {
            building = key;
            isBuilding = true;
            buildNext = 0;
            buildCell = new Cell();
            ResetBuffers();
            cellTrees.Clear();
            Tile tile = tiles[key.terrain];
            int per = CellTiles / bucketTiles, cx = key.index % CellsPerSide, cy = key.index / CellsPerSide;
            for (int by = cy * per; by < (cy + 1) * per; by++)
                for (int bx = cx * per; bx < (cx + 1) * per; bx++)
                    if (tile.buckets[by * bucketsPerSide + bx] != null)
                        cellTrees.AddRange(tile.buckets[by * bucketsPerSide + bx]);
            // Nearest the player first: the GPU then rejects most leaf pixels behind them before shading (alpha-tested leaves get only
            // limited hidden-surface removal), in whichever direction the player looks. Unity draws the cells near to far
            Vector3 player = GameManager.Instance.PlayerObject.transform.position - key.terrain.transform.position;
            cellTrees.Sort((a, b) => (a.position - player).sqrMagnitude.CompareTo((b.position - player).sqrMagnitude));
        }

        void ResetBuffers()
        {
            vertices.Clear();
            normals.Clear();
            uvs.Clear();
            colours.Clear();
            foreach (List<int> list in triangles.Values)
                list.Clear();
        }

        void AddTree(Tile tile, Instance tree)
        {
            TreeModel treeModel = treeModels[(tree.archive != 0 ? tree.archive : tile.archive) << 8 | tree.record];
            Model model = models[treeModel.model];
            if (vertices.Count + model.vertices.Length > maxVertices)
                FlushMesh();
            Quaternion rotation = Quaternion.Euler(0, tree.rotation * Mathf.Rad2Deg, 0);
            int first = vertices.Count;
            for (int i = 0; i < model.vertices.Length; i++)
            {
                vertices.Add(tree.position + rotation * (model.vertices[i] * (tree.scale * treeModel.scale)));
                normals.Add(rotation * model.normals[i]);
                uvs.Add(model.uvs[i]);
                colours.Add(Color.white);
            }
            for (int s = 0; s < Mathf.Min(model.triangles.Length, treeModel.atlas.Length); s++)
            {
                if (NoLeaves && treeModel.cutout[s])
                    continue;
                int key = treeModel.atlas[s] << 1 | (treeModel.cutout[s] ? 1 : 0);
                if (RgbaLeaves && treeModel.cutout[s])
                    key |= rgbaKey;
                List<int> list;
                if (!triangles.TryGetValue(key, out list))
                    triangles[key] = list = new List<int>();
                // Material colour only, no TreeInstance colour: the mod's SpeedTree (v7) and Standard materials never read it on PC
                // (albedo = texture x _Color x vertex colour red, and the meshes have no vertex colours). Lerp(white, grey, tint) left
                // every tree 0-50% darker
                Color32 colour = treeModel.colour[s];
                int[] tris = model.triangles[s];
                for (int i = 0; i < tris.Length; i++)
                {
                    list.Add(first + tris[i]);
                    colours[first + tris[i]] = colour;
                }
            }
        }

        void FlushMesh()
        {
            if (vertices.Count == 0)
                return;
            var mesh = new Mesh();
            mesh.name = "VitaNearTrees";
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colours);
            var keys = new List<int>();
            foreach (var pair in triangles)
                if (pair.Value.Count > 0)
                    keys.Add(pair.Key);
            mesh.subMeshCount = keys.Count;
            var materials = new Material[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                mesh.SetTriangles(triangles[keys[i]], i, false);
                materials[i] = AcquireAtlas(keys[i]);
                buildCell.atlases.Add(keys[i]);
            }
            mesh.RecalculateBounds();
            // To the GPU now, in this build frame, not on the swap frame's first draw (swap hitches were 101-161 ms); kept readable
            mesh.UploadMeshData(false);

            var go = new GameObject("VitaNearTrees cell");
            go.transform.SetParent(building.terrain.transform, false);
            go.layer = building.terrain.gameObject.layer;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = DaggerfallUnity.Settings.NatureBillboardShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            go.SetActive(false);
            buildCell.objects.Add(go);
            ResetBuffers();
        }

        void FinishCell(Tile tile)
        {
            FlushMesh();
            tile.cells[building.index] = buildCell;
            buildCell = null;
            isBuilding = false;
        }

        // New block on, old cells off and freed, rect moved: all in one frame
        void Swap(DaggerfallTerrain terrain, int cell)
        {
            foreach (CellKey key in shown)
                if (!wanted.Contains(key))
                    ReleaseCell(key.terrain, key.index);
            int meshes = 0, triangleCount = 0;
            foreach (CellKey key in wanted)
            {
                foreach (GameObject go in tiles[key.terrain].cells[key.index].objects)
                {
                    go.SetActive(!Paused);
                    Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
                    meshes++;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                        triangleCount += (int)mesh.GetIndexCount(s) / 3;
                }
            }
            if (LogSwaps)
                Debug.LogFormat("VitaNearTrees: {0} cells shown, {1} meshes, {2} triangles, {3} atlases loaded", wanted.Count, meshes, triangleCount, atlases.Count);
            shown.Clear();
            shown.AddRange(wanted);
            shownTerrain = terrain;
            shownCell = cell;
            // Built but no longer wanted (the player moved on mid-build)
            foreach (var pair in tiles)
                for (int i = 0; i < pair.Value.cells.Length; i++)
                    if (pair.Value.cells[i] != null && !shown.Contains(new CellKey { terrain = pair.Key, index = i }))
                        ReleaseCell(pair.Key, i);
        }

        // The shown block in world xz, with billboard positions (tile index x terrain width / 128) inside it; half a tile of slack
        void SetRect()
        {
            if (Paused || !shownTerrain || !shownTerrain.isActiveAndEnabled || GameManager.Instance.IsPlayerInside)
            {
                Shader.SetGlobalVector(rectId, noRect);
                return;
            }
            float tile = shownTerrain.GetComponent<Terrain>().terrainData.size.x / tileDim;
            int cx = shownCell % CellsPerSide, cy = shownCell / CellsPerSide;
            Vector3 origin = shownTerrain.transform.position;
            Shader.SetGlobalVector(rectId, new Vector4(
                origin.x + ((cx - Ring) * CellTiles - 0.5f) * tile, origin.z + ((cy - Ring) * CellTiles - 0.5f) * tile,
                origin.x + ((cx + Hi + 1) * CellTiles - 0.5f) * tile, origin.z + ((cy + Hi + 1) * CellTiles - 0.5f) * tile));
        }

        static bool FindCell(Vector3 position, out DaggerfallTerrain terrain, out int cell)
        {
            foreach (var pair in tiles)
            {
                if (!pair.Key || !pair.Key.isActiveAndEnabled)
                    continue;
                Vector3 local = position - pair.Key.transform.position;
                float size = pair.Key.GetComponent<Terrain>().terrainData.size.x;
                if (local.x < 0 || local.z < 0 || local.x >= size || local.z >= size)
                    continue;
                terrain = pair.Key;
                // Same split as Add (tile index / 16), with the tile index nearest the position as the billboards are placed
                int x = Mathf.Clamp(Mathf.RoundToInt(local.x / size * tileDim), 0, tileDim - 1);
                int y = Mathf.Clamp(Mathf.RoundToInt(local.z / size * tileDim), 0, tileDim - 1);
                cell = y / CellTiles * CellsPerSide + x / CellTiles;
                return true;
            }
            terrain = null;
            cell = -1;
            return false;
        }

        // Destroyed terrains, and reused ones now on another map pixel (StreamingWorld shows the new ground before the new nature)
        void DropDestroyedTiles()
        {
            List<DaggerfallTerrain> gone = null;
            foreach (var pair in tiles)
            {
                DaggerfallTerrain terrain = pair.Key;
                if (!terrain || terrain.MapPixelX != pair.Value.mapPixelX || terrain.MapPixelY != pair.Value.mapPixelY)
                    (gone ?? (gone = new List<DaggerfallTerrain>())).Add(terrain);
            }
            if (gone != null)
                foreach (DaggerfallTerrain terrain in gone)
                    ReleaseTile(terrain);
        }

        static void ReleaseTile(DaggerfallTerrain terrain)
        {
            Tile tile;
            if (!tiles.TryGetValue(terrain, out tile))
                return;
            for (int i = 0; i < tile.cells.Length; i++)
                ReleaseCell(terrain, i);
            tiles.Remove(terrain);
            foreach (VitaLocationTrees owner in locationOwners)
                if (owner && owner.terrain == terrain)
                    owner.terrain = null;   // its trees went with the tile: registered again into the tile laid out next
            if (instance)
            {
                if (instance.isBuilding && instance.building.terrain == terrain)
                    instance.isBuilding = false;
                if (instance.shownTerrain == terrain || instance.shown.Exists(k => k.terrain == terrain))
                    instance.Forget();
            }
        }

        static void ReleaseCell(DaggerfallTerrain terrain, int index)
        {
            Tile tile;
            if (!tiles.TryGetValue(terrain, out tile) || tile.cells[index] == null)
                return;
            foreach (GameObject go in tile.cells[index].objects)
            {
                if (!go)
                    continue;
                Destroy(go.GetComponent<MeshFilter>().sharedMesh);
                Destroy(go);
            }
            foreach (int key in tile.cells[index].atlases)
                ReleaseAtlas(key);
            tile.cells[index] = null;
        }

        static Material AcquireAtlas(int key)
        {
            Atlas atlas;
            if (!atlases.TryGetValue(key, out atlas))
            {
                atlas = new Atlas();
                // StreamingAssets/VitaTrees/atlasN.bytes read whole into native memory: TextAsset.bytes was a 0.35-1.4 MB managed
                // copy per atlas in play, and such large arrays grow the never-shrinking heap
                System.IntPtr fileMemory, file;
                int fileLength;
                string path = Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaTrees"), "atlas" + ((key & ~rgbaKey) >> 1) + ".bytes");
                if (path.StartsWith("/app0/"))
                    path = "app0:" + path.Substring(6);     // sceIoOpen wants the device form
                bool loaded = VitaAtlasCache.ReadFile(path, out fileMemory, out file, out fileLength) && fileLength > 4 + 1024;
                if (!loaded)
                {
                    Debug.LogWarningFormat("VitaNearTrees: {0} not read", path);
                    if (fileMemory != System.IntPtr.Zero)
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(fileMemory);
                }
                if (loaded)
                {
                    int dim = System.Runtime.InteropServices.Marshal.ReadInt32(file);
                    atlas.palette = new Texture2D(256, 1, TextureFormat.RGBA32, false);
                    atlas.palette.filterMode = FilterMode.Point;
                    atlas.palette.wrapMode = TextureWrapMode.Clamp;
                    var palette = new byte[256 * 4];
                    System.Runtime.InteropServices.Marshal.Copy(System.IntPtr.Add(file, 4), palette, 0, palette.Length);
                    atlas.palette.LoadRawTextureData(palette);
                    atlas.palette.Apply(false, true);
                    int offset = 4 + palette.Length, size = fileLength - offset;
                    try
                    {
                        System.IntPtr indices = System.IntPtr.Add(file, offset);
                        // An RGBA atlas is 5.6 MB (+ the same again while expanding): with main memory short (leaving the Sentinel/
                        // Daggerfall Palace, 22-30 MB free) that allocation failed and the game died in SharedTextureData.
                        // Then this atlas keeps the palette look for its lifetime (1.4 MB; slower leaves, same pixels by eye).
                        atlas.rgba = (key & rgbaKey) != 0 && VitaMemory.FreeMain() >= RgbaMinFreeMain;
                        if ((key & rgbaKey) != 0 && !atlas.rgba)
                            Debug.LogFormat("VITATREES atlas {0}: palette leaves, free main {1} MB", (key & ~rgbaKey) >> 1, VitaMemory.FreeMain() >> 20);
                        if (atlas.rgba)
                            atlas.index = ExpandToRgba(indices, size, dim, palette);
                        else
                            atlas.index = VitaPalette.LoadRaw(indices, size, dim, dim, true, false);
                    }
                    finally
                    {
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(fileMemory);
                    }
                }
                if (atlas.rgba)
                {
                    atlas.material = new Material(Shader.Find("Daggerfall/Vita/TreeLeavesRGBA"));
                    atlas.material.mainTexture = atlas.index;
                }
                else
                {
                    atlas.material = new Material(Shader.Find((key & 1) != 0 ? "Daggerfall/Vita/TreeLeavesPalette" : "Daggerfall/Vita/TreePalette"));
                    atlas.material.mainTexture = atlas.index;
                    atlas.material.SetTexture(VitaPalette.PaletteTex, atlas.palette);
                }
                atlas.material.name = "VitaNearTrees " + ((key & ~rgbaKey) >> 1);
                atlases[key] = atlas;
            }
            atlas.refs++;
            atlas.releasedAt = -1;
            return atlas.material;
        }

        // Every mip level of the index data through the palette: RGBA32 with the same mips (index 0 = transparent), bilinear.
        // Expanded into a native buffer freed right after the upload: the old static managed buffer kept 5.6 MB on the IL2CPP heap
        // for good, and atlases of another size made a further copy each time.
        static unsafe Texture2D ExpandToRgba(System.IntPtr indices, int count, int dim, byte[] palette)
        {
            // Transparent texels get the palette's mean colour (still alpha 0): black ones would filter into dark leaf edges
            uint* lut = stackalloc uint[256];
            int r = 0, g = 0, b = 0;
            for (int i = 1; i < 256; i++)
            {
                r += palette[i * 4];
                g += palette[i * 4 + 1];
                b += palette[i * 4 + 2];
                lut[i] = (uint)(palette[i * 4] | palette[i * 4 + 1] << 8 | palette[i * 4 + 2] << 16 | palette[i * 4 + 3] << 24);
            }
            lut[0] = (uint)(r / 255 | (g / 255) << 8 | (b / 255) << 16);
            System.IntPtr rgba = System.Runtime.InteropServices.Marshal.AllocHGlobal(count * 4);
            try
            {
                byte* src = (byte*)indices;
                uint* dst = (uint*)rgba;
                for (int i = 0; i < count; i++)
                    dst[i] = lut[src[i]];
                var texture = new Texture2D(dim, dim, TextureFormat.RGBA32, true);
                texture.filterMode = FilterMode.Bilinear;
                texture.LoadRawTextureData(rgba, count * 4);
                texture.Apply(false, true);
                return texture;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(rgba);
            }
        }

        static void ReleaseAtlas(int key)
        {
            Atlas atlas;
            if (!atlases.TryGetValue(key, out atlas) || --atlas.refs > 0)
                return;
            // Kept a while: cell swaps near a cell edge released and re-read the same atlas (1.4 MB from the card, in LateUpdate) 9 times in
            // 5 s on a Daggerfall walk. DropReleasedAtlases frees it once nothing took it back for keepReleased seconds.
            atlas.releasedAt = Time.realtimeSinceStartup;
        }

        const float keepReleased = 10f;
        static readonly List<int> droppedAtlases = new List<int>();

        static void DropReleasedAtlases()
        {
            float now = Time.realtimeSinceStartup;
            droppedAtlases.Clear();
            foreach (var pair in atlases)
                if (pair.Value.refs <= 0 && pair.Value.releasedAt >= 0 && now - pair.Value.releasedAt > keepReleased)
                    droppedAtlases.Add(pair.Key);
            foreach (int key in droppedAtlases)
            {
                Atlas atlas = atlases[key];
                Destroy(atlas.material);
                Destroy(atlas.index);
                Destroy(atlas.palette);
                atlases.Remove(key);
            }
        }

        // Location trees: PC DFU swaps each RMB scenery tree for the mod's model at any distance (MeshReplacement.
        // ImportCustomFlatGameobject: prefab root scale, rotation Random.InitState((int)x) + Range(0, 360), base at the flat's position).
        // Here they join the terrain trees: each goes into a second billboard batch of the location (VITA_NEAR_TREES: collapsed inside the
        // model block) and, once its block is placed, into the bucket of the terrain under it, so the near cells build its model with
        // the terrain trees around the player and the billboard shows beyond. All of them as models cost 9.7 FPS at one test stop.
        static readonly List<VitaLocationTrees> locationOwners = new List<VitaLocationTrees>();
        static readonly HashSet<DaggerfallBillboardBatch> dirtyBatches = new HashSet<DaggerfallBillboardBatch>();
        public static int LocationTreeCount;

        /// <summary>From RMBLayout.AddNatureFlats: true if this scenery tree has a model (then it goes into the location's near-tree batch,
        /// not billboardBatch)</summary>
        public static bool AddLocationTree(int archive, int record, Vector3 localPosition, Transform parent, DaggerfallBillboardBatch billboardBatch)
        {
            if (!Application.isPlaying || !billboardBatch || !DaggerfallUnity.Settings.VitaLowPolyTrees || !Load() ||
                !treeModels.ContainsKey(archive << 8 | record))
                return false;
            DaggerfallBillboardBatch treeBatch = null;
            foreach (Transform child in billboardBatch.transform)
                if (treeBatch = child.GetComponent<DaggerfallBillboardBatch>())
                    break;
            if (!treeBatch)
            {
                var go = new GameObject("VitaNearTrees");
                go.transform.SetParent(billboardBatch.transform, false);
                go.layer = billboardBatch.gameObject.layer;
                treeBatch = go.AddComponent<DaggerfallBillboardBatch>();
            }
            treeBatch.SetMaterial(archive);
            Material material = treeBatch.GetComponent<MeshRenderer>().sharedMaterial;
            if (material && !material.IsKeywordEnabled(keyword))
                material.EnableKeyword(keyword);
            treeBatch.BlockOrigin = billboardBatch.BlockOrigin;
            treeBatch.AddItem(record, localPosition);
            dirtyBatches.Add(treeBatch);

            VitaLocationTrees owner = parent.GetComponent<VitaLocationTrees>();
            if (!owner)
            {
                owner = parent.gameObject.AddComponent<VitaLocationTrees>();
                locationOwners.Add(owner);
            }
            Random.State state = Random.state;
            Random.InitState((int)localPosition.x);
            float rotation = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            Random.state = state;   // upstream without the mod draws nothing here
            owner.trees.Add(new Instance { record = record, archive = archive, position = localPosition, scale = 1, rotation = rotation, owner = owner });
            LocationTreeCount++;
            if (!instance)
                instance = new GameObject("VitaNearTrees").AddComponent<VitaNearTrees>();
            return true;
        }

        // Batches applied once their location is laid out; owners' trees put into the bucket of the terrain under them once placed
        void UpdateLocationTrees()
        {
            if (dirtyBatches.Count > 0)
            {
                foreach (DaggerfallBillboardBatch batch in dirtyBatches)
                    if (batch)
                        batch.Apply();
                dirtyBatches.Clear();
            }
            bool added = false;
            locationOwners.RemoveAll(o => !o);
            foreach (VitaLocationTrees owner in locationOwners)
            {
                if (owner.terrain || owner.trees.Count == 0)
                    continue;
                DaggerfallTerrain terrain;
                int cell;
                Vector3 first = owner.transform.TransformPoint(owner.trees[0].position);
                if (!FindCell(first, out terrain, out cell))
                    continue;   // its terrain isn't laid out yet
                Tile tile = tiles[terrain];
                float size = terrain.GetComponent<Terrain>().terrainData.size.x;
                foreach (Instance local in owner.trees)
                {
                    Instance tree = local;
                    tree.position = owner.transform.TransformPoint(local.position) - terrain.transform.position;
                    int x = Mathf.Clamp(Mathf.RoundToInt(tree.position.x / size * tileDim), 0, tileDim - 1);
                    int y = Mathf.Clamp(Mathf.RoundToInt(tree.position.z / size * tileDim), 0, tileDim - 1);
                    int bucket = y / bucketTiles * bucketsPerSide + x / bucketTiles;
                    if (tile.buckets[bucket] == null)
                        tile.buckets[bucket] = new List<Instance>();
                    tile.buckets[bucket].Add(tree);
                }
                owner.terrain = terrain;
                added = true;
            }
            if (added)
                Forget();   // cells rebuilt with them (billboards show meanwhile)
        }

        /// <summary>From VitaLocationTrees.OnDestroy: its trees out of the buckets</summary>
        internal static void RemoveLocationTrees(VitaLocationTrees owner)
        {
            locationOwners.Remove(owner);
            Tile tile;
            if (!owner.terrain || !tiles.TryGetValue(owner.terrain, out tile))
                return;
            foreach (List<Instance> bucket in tile.buckets)
                if (bucket != null)
                    bucket.RemoveAll(i => i.owner == owner);
            if (instance)
                instance.Forget();
        }

        static bool HasModels(int archive)
        {
            for (int record = 0; record < 256; record++)
                if (treeModels.ContainsKey(archive << 8 | record))
                    return true;
            return false;
        }

        // trees.bytes (vita/tools/lowpolytrees.py), once
        static bool Load()
        {
            if (loaded || loadFailed)
                return loaded;
            var asset = Resources.Load<TextAsset>("VitaTrees/trees");
            if (!asset)
            {
                loadFailed = true;
                Debug.LogWarning("VitaNearTrees: Resources/VitaTrees/trees.bytes missing (run vita/tools/lowpolytrees.py)");
                return false;
            }
            byte[] bytes = asset.bytes;
            Resources.UnloadAsset(asset);
            using (var reader = new BinaryReader(new MemoryStream(bytes)))
            {
                reader.ReadBytes(4);    // "VLPT"
                reader.ReadInt32();     // version
                models = new Model[reader.ReadInt32()];
                int treeCount = reader.ReadInt32();
                for (int m = 0; m < models.Length; m++)
                {
                    var model = new Model();
                    int count = reader.ReadInt32();
                    model.vertices = new Vector3[count];
                    model.normals = new Vector3[count];
                    model.uvs = new Vector2[count];
                    for (int i = 0; i < count; i++)
                        model.vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    for (int i = 0; i < count; i++)
                        model.normals[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    for (int i = 0; i < count; i++)
                        model.uvs[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    model.triangles = new int[reader.ReadInt32()][];
                    for (int s = 0; s < model.triangles.Length; s++)
                    {
                        model.triangles[s] = new int[reader.ReadInt32()];
                        for (int i = 0; i < model.triangles[s].Length; i++)
                            model.triangles[s][i] = reader.ReadUInt16();
                    }
                    models[m] = model;
                }
                for (int t = 0; t < treeCount; t++)
                {
                    int archive = reader.ReadInt16(), record = reader.ReadInt16();
                    var tree = new TreeModel { model = reader.ReadInt32() };
                    tree.scale = reader.ReadSingle();   // prefab root scale: the mod sizes each tree with it (0.6-4.2), PC applies it
                    int subs = reader.ReadInt32();
                    tree.atlas = new int[subs];
                    tree.cutout = new bool[subs];
                    tree.colour = new Color32[subs];
                    for (int s = 0; s < subs; s++)
                    {
                        tree.atlas[s] = reader.ReadInt32();
                        tree.cutout[s] = reader.ReadByte() != 0;
                        tree.colour[s] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), 255);
                    }
                    treeModels[archive << 8 | record] = tree;
                }
            }
            loaded = true;
            Debug.LogFormat("VitaNearTrees: {0} models, {1} trees", models.Length, treeModels.Count);
            return true;
        }
    }

    /// <summary>One RMB block's scenery trees that have a model (VitaNearTrees.AddLocationTree), in the block's flats node space</summary>
    public class VitaLocationTrees : MonoBehaviour
    {
        internal readonly List<VitaNearTrees.Instance> trees = new List<VitaNearTrees.Instance>();
        internal DaggerfallTerrain terrain;     // whose buckets hold them; null = not yet

        void OnDestroy()
        {
            VitaNearTrees.RemoveLocationTrees(this);
        }
    }
}
#endif
