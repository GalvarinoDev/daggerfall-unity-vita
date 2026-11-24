// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:    Hazelnut
// 
// Notes:
//

using UnityEngine;
using System;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop.Utility;
using Unity.Collections;
using Unity.Jobs;
using System.Collections.Generic;

namespace DaggerfallWorkshop
{
    /// <summary>
    /// Partners with a Unity Terrain for use by StreamingWorld.
    /// Each terrain is "self-assembling" based on position in world (1000x500 map pixels).
    /// Also serializes additional information about neighbour terrains.
    /// </summary>
    [RequireComponent(typeof(Terrain))]
    [RequireComponent(typeof(TerrainCollider))]
    public class DaggerfallTerrain : MonoBehaviour
    {
        // Settings are tuned for Daggerfall and fast procedural layout
        const int tilemapDim = MapsFile.WorldMapTileDim;
        const int resolutionPerPatch = 16;

        // This controls which map pixel the terrain will represent
        [Range(TerrainHelper.minMapPixelX, TerrainHelper.maxMapPixelX)]
        public int MapPixelX = TerrainHelper.defaultMapPixelX;
        [Range(TerrainHelper.minMapPixelY, TerrainHelper.maxMapPixelY)]
        public int MapPixelY = TerrainHelper.defaultMapPixelY;

        // Increasing scale will amplify terrain height
        // Must be set per terrain for correct tiling
        [Range(TerrainHelper.minTerrainScale, TerrainHelper.maxTerrainScale)]
        public float TerrainScale = TerrainHelper.defaultTerrainScale;

        // Data for this terrain
        public MapPixelData MapData;

        // Neighbours of this terrain
        public Terrain LeftNeighbour;
        public Terrain TopNeighbour;
        public Terrain RightNeighbour;
        public Terrain BottomNeighbour;

        // The tile map
        [NonSerialized]
        public Color32[] TileMap;

        // Required for material properties
        [SerializeField, HideInInspector]
        Texture2D tileMapTexture;
        [SerializeField, HideInInspector]
        Material terrainMaterial;

        public Material TerrainMaterial { get { return terrainMaterial; } set { terrainMaterial = value; } }

        DaggerfallUnity dfUnity;
        int heightmapDim;
        int currentWorldClimate = -1;
        DaggerfallDateTime.Seasons season = DaggerfallDateTime.Seasons.Summer;
        bool ready;

        private float heightMapPixelError = 5; // just a default value in case ini value reading fails (so value will be overwritten by ini file value)

        public float HeightMapPixelError
        {
            get { return heightMapPixelError; }
            set { heightMapPixelError = value; }
        }

        // Dispose any native memory if class is destroyed.
        ~DaggerfallTerrain()
        {
            DisposeNativeMemory();
        }

        void Awake()
        {
            HeightMapPixelError = DaggerfallUnity.Settings.TerrainHeightmapPixelError;
        }

        void Start()
        {
            UpdateNeighbours();
            ready = false;
#if UNITY_PSP2
        }

        void OnDestroy()
        {
            // Runtime-made and owned by this terrain (usually pooled forever, but destroyed when the world is cleared)
            if (tileMapTexture)
                Destroy(tileMapTexture);
            if (terrainMaterial)
                Destroy(terrainMaterial);
            Terrain terrain = GetComponent<Terrain>();
            if (terrain && terrain.terrainData)
            {
                // Fast travel and dungeon entry destroy every terrain, and the next world makes new TerrainData for each
                // (67-72 ms per new tile in play): keep a few for reuse. Same resolution every time.
                if (!VitaTerrainDataPoolOff && vitaTerrainDataPool.Count < VitaTerrainDataPoolMax)
                    vitaTerrainDataPool.Push(terrain.terrainData);
                else
                    Destroy(terrain.terrainData);
            }
#endif
        }

#if UNITY_PSP2
        public static bool VitaTerrainDataPoolOff;      // test harness A/B: new TerrainData per terrain, as upstream
        const int VitaTerrainDataPoolMax = 9;
        static readonly Stack<TerrainData> vitaTerrainDataPool = new Stack<TerrainData>();
#endif

        /// <summary>
        /// This must be called when first creating terrain or before updating terrain.
        /// Safe to call multiple times. Recreates expired volatile objects on subsequent calls.
        /// </summary>
        public void InstantiateTerrain()
        {
            if (!ReadyCheck())
                return;

            // Create tileMap texture
            if (tileMapTexture == null)
            {
                tileMapTexture = new Texture2D(tilemapDim, tilemapDim, TextureFormat.ARGB32, false, true);
                tileMapTexture.filterMode = FilterMode.Point;
                tileMapTexture.wrapMode = TextureWrapMode.Clamp;
            }

            // Create terrain material
            if (terrainMaterial == null)
            {
                terrainMaterial = dfUnity.TerrainMaterialProvider.CreateMaterial();
                UpdateClimateMaterial();
            }

            // Raise event
            RaiseOnInstantiateTerrainEvent();
        }

        /// <summary>
        /// Updates climate material based on current map pixel data.
        /// Use <see cref="PromoteTerrainData()"/> to apply changes.
        /// </summary>
        public void UpdateClimateMaterial(bool init = false)
        {
            // Update atlas texture if world climate changed
            if (currentWorldClimate != MapData.worldClimate || dfUnity.WorldTime.Now.SeasonValue != season || init)
            {
                currentWorldClimate = MapData.worldClimate;
            }
        }

        /// <summary>
        /// Update map pixel data based on current coordinates. (first of a two stage process)
        /// 
        /// 1) BeginMapPixelDataUpdate - Schedules terrain data update using jobs system.
        /// 2) CompleteMapPixelDataUpdate - Completes terrain data update using jobs system.
        /// </summary>
        /// <param name="terrainTexturing">Instance of ITerrainTexturing implementation class to use.</param>
        /// <returns>JobHandle of the scheduled jobs</returns>
        public JobHandle BeginMapPixelDataUpdate(ITerrainTexturing terrainTexturing = null)
        {
            // Get basic terrain data.
            MapData = TerrainHelper.GetMapPixelData(dfUnity.ContentReader, MapPixelX, MapPixelY);

            // Create data array for heightmap.
            MapData.heightmapData = new NativeArray<float>(heightmapDim * heightmapDim, Allocator.TempJob);

            // Create data array for tilemap data.
            MapData.tilemapData = new NativeArray<byte>(tilemapDim * tilemapDim, Allocator.TempJob);

            // Create data array for shader tile map data.
            MapData.tileMap = new NativeArray<Color32>(tilemapDim * tilemapDim, Allocator.TempJob);

            // Create data array for average & max heights.
            MapData.avgMaxHeight = new NativeArray<float>(new float[] { 0, float.MinValue }, Allocator.TempJob);

            // Create list for recording native arrays that need disposal after jobs complete.
            MapData.nativeArrayList = new List<IDisposable>();

            // Generate heightmap samples. (returns when complete)
            JobHandle generateHeightmapSamplesJobHandle = dfUnity.TerrainSampler.ScheduleGenerateSamplesJob(ref MapData);

            // Handle location if one is present on terrain.
            JobHandle blendLocationTerrainJobHandle;
            if (MapData.hasLocation)
            {
                // Schedule job to calc average & max heights.
                JobHandle calcAvgMaxHeightJobHandle = TerrainHelper.ScheduleCalcAvgMaxHeightJob(ref MapData, generateHeightmapSamplesJobHandle);
                JobHandle.ScheduleBatchedJobs();

                // Set location tiles.
                TerrainHelper.SetLocationTiles(ref MapData);

                if (!dfUnity.TerrainSampler.IsLocationTerrainBlended())
                {
                    // Schedule job to blend and flatten location heights. (depends on SetLocationTiles being done first)
                    blendLocationTerrainJobHandle = TerrainHelper.ScheduleBlendLocationTerrainJob(ref MapData, calcAvgMaxHeightJobHandle);
                }
                else
                    blendLocationTerrainJobHandle = calcAvgMaxHeightJobHandle;
            }
            else
                blendLocationTerrainJobHandle = generateHeightmapSamplesJobHandle;

            // Assign tiles for terrain texturing.
            JobHandle assignTilesJobHandle = (terrainTexturing == null) ? blendLocationTerrainJobHandle :
                terrainTexturing.ScheduleAssignTilesJob(dfUnity.TerrainSampler, ref MapData, blendLocationTerrainJobHandle);

            // Update tile map for shader.
            JobHandle updateTileMapJobHandle = TerrainHelper.ScheduleUpdateTileMapDataJob(ref MapData, assignTilesJobHandle);
            JobHandle.ScheduleBatchedJobs();
            return updateTileMapJobHandle;
        }

        /// <summary>
        /// Complete terrain data update using jobs system. (second of a two stage process)
        /// </summary>
        /// <param name="terrainTexturing">Instance of ITerrainTexturing implementation class to use.</param>
        public void CompleteMapPixelDataUpdate(ITerrainTexturing terrainTexturing = null)
        {
#if UNITY_PSP2
            // Vita: the same copies by row and column from the stand-in arrays' storage: Row/Col are an integer % and / per sample (no
            // hardware divide), and with the indexer calls the two loops were ~85-160 ms of one in-play frame per new tile
            MapData.heightmapSamples = new float[heightmapDim, heightmapDim];
            float[] vitaHeights = MapData.heightmapData.VitaArray;
            for (int col = 0, i = 0; col < heightmapDim; col++)
                for (int row = 0; row < heightmapDim; row++, i++)
                    MapData.heightmapSamples[row, col] = vitaHeights[i];

            MapData.tilemapSamples = new byte[tilemapDim, tilemapDim];
            byte[] vitaTiles = MapData.tilemapData.VitaArray;
            for (int col = 0, i = 0; col < tilemapDim; col++)
                for (int row = 0; row < tilemapDim; row++, i++)
                {
                    byte tile = vitaTiles[i];
                    MapData.tilemapSamples[row, col] = tile == byte.MaxValue ? (byte)0 : tile;
                }
#else
            // Convert heightmap data back to standard managed 2d array.
            MapData.heightmapSamples = new float[heightmapDim, heightmapDim];
            for (int i = 0; i < MapData.heightmapData.Length; i++)
                MapData.heightmapSamples[JobA.Row(i, heightmapDim), JobA.Col(i, heightmapDim)] = MapData.heightmapData[i];

            // Convert tilemap data back to standard managed 2d array.
            // (Still needed for nature layout so it can be called again without requiring terrain data generation)
            MapData.tilemapSamples = new byte[tilemapDim, tilemapDim];
            for (int i = 0; i < MapData.tilemapData.Length; i++)
            {
                byte tile = MapData.tilemapData[i];
                if (tile == byte.MaxValue)
                    tile = 0;
                MapData.tilemapSamples[JobA.Row(i, tilemapDim), JobA.Col(i, tilemapDim)] = tile;
            }
#endif

            // Create tileMap array or resize if needed and copy native array.
            if (TileMap == null || TileMap.Length != MapData.tileMap.Length)
                TileMap = new Color32[MapData.tileMap.Length];
            MapData.tileMap.CopyTo(TileMap);

            // Copy max and avg heights. (TODO: Are these needed? Seem to not be used anywhere)
            MapData.averageHeight = MapData.avgMaxHeight[TerrainHelper.avgHeightIdx];
            MapData.maxHeight = MapData.avgMaxHeight[TerrainHelper.maxHeightIdx];

            DisposeNativeMemory();
        }

        /// <summary>
        /// Disposes the native arrays used in jobs system terrain data update.
        /// </summary>
        private void DisposeNativeMemory()
        {
            if (MapData.nativeArrayList != null)
            {
                // Dispose any temp working native array memory.
                foreach (IDisposable nativeArray in MapData.nativeArrayList)
                    nativeArray.Dispose();
                MapData.nativeArrayList = null;
            }

            // Dispose native array memory allocations now data has been extracted.
            if (MapData.heightmapData.IsCreated)
                MapData.heightmapData.Dispose();
            if (MapData.tilemapData.IsCreated)
                MapData.tilemapData.Dispose();
            if (MapData.avgMaxHeight.IsCreated)
                MapData.avgMaxHeight.Dispose();
            if (MapData.tileMap.IsCreated)
                MapData.tileMap.Dispose();
        }

        /// <summary>
        /// Promote data to live terrain.
        /// This must be called after other processing complete.
        /// </summary>
        public void PromoteTerrainData()
        {
            // Basemap not used and is just pushed far away
            const float basemapDistance = 10000f;
            int heightmapDimension = dfUnity.TerrainSampler.HeightmapDimension;
            int detailResolution = heightmapDimension;

            // Ensure TerrainData is created
            Terrain terrain = GetComponent<Terrain>();
#if UNITY_PSP2
#if VITA_PROBES
            long vitaL0 = System.Diagnostics.Stopwatch.GetTimestamp();   // promote laps
#endif
            while (terrain.terrainData == null && vitaTerrainDataPool.Count > 0)
            {
                TerrainData pooled = vitaTerrainDataPool.Pop();
                if (pooled && pooled.heightmapResolution == heightmapDimension)
                {
                    terrain.terrainData = pooled;
                    terrain.GetComponent<TerrainCollider>().terrainData = pooled;
                    terrain.basemapDistance = basemapDistance;
                    terrain.heightmapPixelError = heightMapPixelError;
                }
                else if (pooled)
                    Destroy(pooled);
            }
#endif
            if (terrain.terrainData == null)
            {
                // Calculate width and length of terrain in world units
                float terrainSize = (MapsFile.WorldMapTerrainDim * MeshReader.GlobalScale);

                // Setup terrain data
                // Must set terrainData.heightmapResolution before size (thanks Nystul!)
                TerrainData terrainData = new TerrainData();
                terrainData.name = "TerrainData";
                terrainData.heightmapResolution = heightmapDimension;
                terrainData.size = new Vector3(terrainSize, dfUnity.TerrainSampler.MaxTerrainHeight, terrainSize);
#if UNITY_PSP2
                // No grass/detail layers on the Vita (nothing calls SetDetailLayer): smallest detail map
                terrainData.SetDetailResolution(resolutionPerPatch, resolutionPerPatch);
#else
                terrainData.SetDetailResolution(detailResolution, resolutionPerPatch);
#endif
#if UNITY_PSP2
                // The Vita terrain draws with its own tilemap material (materialType Custom below) and pushes the basemap 10 km away,
                // so the splat map and basemap are never shown: the smallest sizes Unity allows. 129 each cost 46 ms per new terrain
                // object in play plus their memory.
                terrainData.alphamapResolution = 16;
                terrainData.baseMapResolution = 16;
#else
                terrainData.alphamapResolution = detailResolution;
                terrainData.baseMapResolution = detailResolution;
#endif

                // Apply terrain data
                terrain.terrainData = terrainData;
                terrain.GetComponent<TerrainCollider>().terrainData = terrainData;
                terrain.basemapDistance = basemapDistance;
                terrain.heightmapPixelError = heightMapPixelError;
            }

#if UNITY_PSP2 && VITA_PROBES
            long vitaL1 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            // Promote tileMap
            tileMapTexture.SetPixels32(TileMap);
            tileMapTexture.Apply(false);

#if UNITY_PSP2 && VITA_PROBES
            long vitaL2 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            // Promote material
            var terrainMaterialData = new TerrainMaterialData(terrainMaterial, terrain.terrainData, tileMapTexture, currentWorldClimate);
            dfUnity.TerrainMaterialProvider.PromoteMaterial(this, terrainMaterialData);
#if UNITY_PSP2
            // Unity 2017 draws a terrain with materialTemplate only when materialType is Custom (2019 dropped the property, so upstream never
            // sets it); otherwise it uses the built-in terrain shader with default white splat textures: the plain white/grey ground seen
            // in every city
            terrain.materialType = Terrain.MaterialType.Custom;
#endif
            terrain.materialTemplate = terrainMaterial;

            // Promote heights
#if UNITY_PSP2 && VITA_PROBES
            long vitaL3 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            Vector3 size = terrain.terrainData.size;
#if UNITY_PSP2
            // Setting the size costs ~4 ms on every promote even when it doesn't change (pooled TerrainData keeps it)
            Vector3 vitaSize = new Vector3(size.x, dfUnity.TerrainSampler.MaxTerrainHeight * TerrainScale, size.z);
            if (vitaSize != size)
                terrain.terrainData.size = vitaSize;
#else
            terrain.terrainData.size = new Vector3(size.x, dfUnity.TerrainSampler.MaxTerrainHeight * TerrainScale, size.z);
#endif
#if UNITY_PSP2
#if VITA_PROBES
            long vitaL4 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            // Tiles built in play: the heights now, Unity's LOD/patch bounds on the next frame (StreamingWorld calls
            // ApplyDelayedHeightmapModification before it shows the tile). SetHeights did both in one ~45 ms step.
            // VitaHeightStrips > 1 (test): StreamingWorld sets the heights in row strips on frames of their own instead.
            VitaHeightsPending = false;
            if (StreamingWorld.VitaSpreadPromote && !StreamingWorld.VitaDelayLodOff && VitaHeightStrips > 1)
                VitaHeightsPending = true;
            else if (StreamingWorld.VitaSpreadPromote && !StreamingWorld.VitaDelayLodOff)
                terrain.terrainData.SetHeightsDelayLOD(0, 0, MapData.heightmapSamples);
            else
                terrain.terrainData.SetHeights(0, 0, MapData.heightmapSamples);
#else
            terrain.terrainData.SetHeights(0, 0, MapData.heightmapSamples);
#endif

#if UNITY_PSP2 && VITA_PROBES
            long vitaL5 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            // Raise event
            RaiseOnPromoteTerrainDataEvent(terrain.terrainData);
#if UNITY_PSP2 && VITA_PROBES
            if (MaterialReader.VitaProbeLog && StreamingWorld.VitaSpreadPromote)
            {
                long vitaL6 = System.Diagnostics.Stopwatch.GetTimestamp();
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Debug.LogFormat("VITAPROMOTELAPS {0},{1}: data {2:F1}, tilemap {3:F1}, material {4:F1}, size {5:F1} (y {6} -> {7}), heights {8:F1}, event {9:F1} ms",
                    MapPixelX, MapPixelY, (vitaL1 - vitaL0) * f, (vitaL2 - vitaL1) * f, (vitaL3 - vitaL2) * f, (vitaL4 - vitaL3) * f, size.y,
                    terrain.terrainData.size.y, (vitaL5 - vitaL4) * f, (vitaL6 - vitaL5) * f);
            }
#endif
        }

#if UNITY_PSP2
        // test: in-play promote heights in N row strips, one per frame
        public static int VitaHeightStrips = 1;
        [NonSerialized] public bool VitaHeightsPending;
        static float[,] vitaStrip;

        public void VitaSetHeightStrip(int k, int n)
        {
            float[,] all = MapData.heightmapSamples;
            int dim = all.GetLength(0), y0 = dim * k / n, y1 = dim * (k + 1) / n;
            if (vitaStrip == null || vitaStrip.GetLength(0) != y1 - y0 || vitaStrip.GetLength(1) != dim)
                vitaStrip = new float[y1 - y0, dim];
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < dim; x++)
                    vitaStrip[y - y0, x] = all[y, x];
            GetComponent<Terrain>().terrainData.SetHeightsDelayLOD(0, y0, vitaStrip);
        }
#endif

        /// <summary>
        /// Updates neighbour terrains.
        /// </summary>
        public void UpdateNeighbours()
        {
            Terrain terrain = GetComponent<Terrain>();
            terrain.SetNeighbors(LeftNeighbour, TopNeighbour, RightNeighbour, BottomNeighbour);
        }

        #region Editor Support

//#if UNITY_EDITOR
//        /// <summary>
//        /// Allows editor to set terrain independently of StreamingWorld. 
//        /// Mainly for testing purposes, but could be used for static scenes.
//        /// Also shows full terrain setup procedure for reference.
//        /// </summary>
//        public void __EditorUpdateTerrain()
//        {
//            // Setup terrain
//            InstantiateTerrain();

//            // Update data for terrain
//            UpdateMapPixelData();
//            UpdateTileMapData();
//            //UpdateHeightData();

//            // Promote data to live terrain
//            UpdateClimateMaterial();
//            PromoteTerrainData();

//            // Set neighbours
//            UpdateNeighbours();
//        }
//#endif

        #endregion

        #region Private Methods

        private bool ReadyCheck()
        {
            if (ready)
                return true;

            // Ensure we have a DaggerfallUnity reference
            if (dfUnity == null)
            {
                dfUnity = DaggerfallUnity.Instance;
                heightmapDim = dfUnity.TerrainSampler.HeightmapDimension;
            }

            // Do nothing if DaggerfallUnity not ready
            if (!dfUnity.IsReady)
            {
                DaggerfallUnity.LogMessage("DaggerfallTerrain: DaggerfallUnity component is not ready. Have you set your Arena2 path?");
                return false;
            }

            // Raise ready flag
            ready = true;

            return true;
        }

        #endregion

        #region Event Handlers

        // OnInstantiateTerrain
        public delegate void OnInstantiateTerrainEventHandler(DaggerfallTerrain sender);
        public static event OnInstantiateTerrainEventHandler OnInstantiateTerrain;
        protected virtual void RaiseOnInstantiateTerrainEvent()
        {
            if (OnInstantiateTerrain != null)
                OnInstantiateTerrain(this);
        }

        // OnPromoteTerrainData
        public delegate void OnPromoteTerrainDataEventHandler(DaggerfallTerrain sender, TerrainData terrainData);
        public static event OnPromoteTerrainDataEventHandler OnPromoteTerrainData;
        protected virtual void RaiseOnPromoteTerrainDataEvent(TerrainData terrainData)
        {
            if (OnPromoteTerrainData != null)
                OnPromoteTerrainData(this, terrainData);
        }

        #endregion
    }
}