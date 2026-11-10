// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// World of Daggerfall - Terrain 1.5.0 (monobelisk, Freak2121, carademono, Ninelan) on the CPU. The mod makes
// each terrain tile's heights with compute shaders (TerrainComputer.compute, kernel TerrainComputer); the Vita has none, so this is that
// kernel line by line in C#, fed from VitaNative/wodterrain.bin (vita/tools/wodterrainpack.py: the mod's maps as Unity imported them and the
// WOODS heightmap as the mod's MainHeightmapComputer rewrites it at start). Checked against heights dumped from PC DFU running the mod
// (editor check VitaWodTerrainCheck). Parts the shipped data switches off are left out:
//  - the biome map ships all black, so mountain/desert/hill weights are 0 (no Swiss, IQ, dune, rocky or fault noise; the baker checks it);
//  - GetBaseHeight's detailed base sample is always 1 (the mod fills its height buffers with 255), so land = the deriv map's low-res height;
//  - the transition noise is weighted by saturate(((h - 100) / 5000) / 5) on a 0-1 height, which is always 0;
//  - the TilemapComputer kernel only fills a cache other mods can ask for (none of ours do).
// Location weights only visit locations a sample can reach (fade <= 64 * 9 units, radial edge <= half diagonal + 7.5): same result.

#if UNITY_PSP2
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Game;

public class VitaWodTerrainSampler : TerrainSampler
{
    static bool started;

    // settings.ini [Video] VitaWodTerrain = True. Installed where the mod's Init runs (state Start, once per launch), as the mod does: its
    // sampler, the WOODS map as its MainHeightmapComputer rewrites it, terrain scale 1. Not ported: farClipPlane 10000 (the Vita's far
    // view is its own setting) and the mod's console commands / mod messages.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        StateManager.OnStateChange += state =>
        {
            if (state != StateManager.StateTypes.Start || started || !DaggerfallUnity.Settings.VitaWodTerrain)
                return;
            started = true;
            if (!VitaWodTerrain.Load())
                return;
            DaggerfallUnity.Instance.ContentReader.WoodsFileReader.Buffer = VitaWodTerrain.Woods;
            DaggerfallUnity.Instance.TerrainSampler = new VitaWodTerrainSampler();
            GameManager.Instance.StreamingWorld.TerrainScale = 1f;
            Debug.Log("VitaWodTerrain: on");
        };
    }

    public VitaWodTerrainSampler()
    {
        HeightmapDimension = defaultHeightmapDimension;
        MaxTerrainHeight = 5000f;
        MeanTerrainHeightScale = 5000f / 255f;
        OceanElevation = 100.01f;
        BeachElevation = 103.9f;
    }

    public override int Version { get { return 7; } }

    public override bool IsLocationTerrainBlended() { return true; }

    public override void GenerateSamples(ref MapPixelData mapPixel)
    {
        mapPixel.maxHeight = MaxTerrainHeight;
        VitaWodTerrain.Generate(mapPixel.mapPixelX, mapPixel.mapPixelY, mapPixel.heightmapData);
    }

    // As jobs, so a tile built in play goes through VitaJobsShim's deferred pump (a few ms per frame) like the default sampler's
    public override JobHandle ScheduleGenerateSamplesJob(ref MapPixelData mapPixel)
    {
        mapPixel.maxHeight = MaxTerrainHeight;
        VitaWodTerrain tile = new VitaWodTerrain();
        JobHandle begin = new BeginJob { tile = tile, mpx = mapPixel.mapPixelX, mpy = mapPixel.mapPixelY }.Schedule();
        return new SampleJob { tile = tile, data = mapPixel.heightmapData }.Schedule(HeightmapDimension * HeightmapDimension, 64, begin);
    }

    struct BeginJob : IJob
    {
        public VitaWodTerrain tile;
        public int mpx, mpy;
        public void Execute() { tile.Begin(mpx, mpy); }
    }

    struct SampleJob : IJobParallelFor
    {
        public VitaWodTerrain tile;
        public NativeArray<float> data;
        public void Execute(int index)
        {
            data[index] = tile.Sample(index / 129, index % 129);
        }
    }
}

// Shared data is static; one instance per tile holds that tile's location rects, road flags and map window
public sealed class VitaWodTerrain
{
    const int Res = 129;
    const float NewHeight = 5000f;
    const float TerrainX = 999f, TerrainY = 499f;
    const float WorldW = 129f * TerrainX, WorldH = 129f * TerrainY;

    // interesting_terrains.txt values for the generators the tile kernel uses
    const int DuneOct = 4; const float DuneFreq = 0.0015f, DuneAmp = 0.59f, DuneLac = 1.39f, DunePers = 0.61f, DuneOffX = 1000f, DuneOffY = 0f, DuneMax = 40f;
    const int CellOct = 15; const float CellFreq = 0.005f, CellLac = 2f, CellPers = 0.5f, CellRidge = 0.5f, CellWarp = 8.1f, CellMax = 252f;
    const int JordOct = 12; const float JordFreq = 0.001f, JordAmp = 1f, JordLac = 2f, JordPers = 0.5f, JordPers1 = 1f, JordOffX = 5000f, JordOffY = 5000f,
        JordWarp0 = 100f, JordWarp = -200f, JordDamp0 = 1f, JordDamp = 0.3f, JordDampScale = 1f, JordMax = 1548f;
    const int FoldOct = 16; const float FoldFreq = 0.0005f, FoldAmp = 0.9f, FoldLac = 2f, FoldPers = 0.5f, FoldOffX = 0.45f, FoldOffY = 0f, FoldRidge = 1f, FoldWarp = 41.64f, FoldMax = 3131f;
    const int IqOct = 16; const float IqFreq = 0.0008f, IqAmp = 1f, IqLac = 2f, IqPers = 0.5f, IqOffX = 0f, IqOffY = 0f, IqWarp = 1.15f, IqMax = 3224f;
    const int FaultOct = 12; const float FaultFreq = 0.002f, FaultAmp = 3.24f, FaultLac = 2f, FaultPers = 0.5f, FaultOffX = 1500f, FaultOffY = 1000f, FaultRidge = 1f, FaultWarp = 156.85f, FaultMax = 5000f;
    const int DuneSOct = 3; const float DuneSFreq = 0.01f, DuneSAmp = 1.03f, DuneSLac = 1.22f, DuneSPers = 0.25f, DuneSOffX = -500f, DuneSOffY = 400f, DuneSRidge = 0.7f, DuneSWarp = 23.09f, DuneSMax = 60f;
    const int ColorOct = 4; const float ColorFreq = 0.02f, ColorAmp = 1f, ColorLac = 2f, ColorPers = 0.5f;

    static byte[] woods;            // WOODS heights as the mod rewrites them (also the game's WoodsFile buffer while this is on)
    static float[] noise;           // tileable_noise, linear
    static int mapW, mapH;
    static long planeStart;
    static readonly float[] srgb = new float[256];
    static string path;

    // per-tile window of the 2048x1024 maps (deriv b, port r/g/b, road r), linear values
    int winX, winY, winW, winH;
    readonly float[][] win = new float[5][];
    float tpx, tpy;
    internal int mpx, mpy;

    // location rects in tile units (mod: GetLocationRect expanded by 1), per map pixel; width 0 = no location
    static readonly Dictionary<int, Rect> rects = new Dictionary<int, Rect>();
    internal readonly List<Rect> near = new List<Rect>();
    readonly float[] roadBits = new float[9 * 8];

    // Approximations (chosen in the offline harness against heights dumped from PC DFU's GPU: max error 3.4 m, mean 0.32 m over
    // 12 tiles, was 2.0 / 0.31 m exact). Smooth terms on a coarse grid (every CS-th sample), bilinear in between: location blending
    // (both passes), the colour noise's alpha, Jordan's two low-frequency modulators, its first JordSplit octaves and the large dune
    // bumps. Roads stay per sample (coarse: 3.5 m off along them). Octaves past the sample rate are dropped where the GPU's own values
    // already differ (Jordan 12 -> 8 lowered the error), cell noise 15 -> 9 (+0.1 m mean), small bumps 8 -> 6 (0 change).
    // Filled on demand (jobs run samples in x order). Kept as statics so the harness (held tests tools/wodh) can sweep them.
    internal static int CS = 4, CoarseMask = 29;   // 1 locations, 2 roads, 4 colour, 8 Jordan modulators, 16 dunes
    internal static int CellOctUse = 9, JordOctUse = 8, BumpOctUse = 6;
    internal static int CellSplit = 0, JordSplit = 7;   // octaves below these come from the coarse grid (0 = none)
    const int CF = 17;
    int cn;
    float[] coarse = new float[0];
    bool[] coarseDone = new bool[0];

    public static bool Load()
    {
        if (woods != null)
            return true;
        path = Path.Combine(Application.streamingAssetsPath, "VitaNative/wodterrain.bin");
        try
        {
            using (BinaryReader r = new BinaryReader(File.OpenRead(path)))
            {
                if (r.ReadInt32() != 0x54444f57 || r.ReadInt32() != 1)
                    throw new IOException("wodterrain.bin: wrong format");
                mapW = r.ReadInt32();
                mapH = r.ReadInt32();
                woods = r.ReadBytes(1000 * 500);
                byte[] n = r.ReadBytes(128 * 128);
                for (int i = 0; i < 256; i++)
                {
                    float c = i / 255f;
                    srgb[i] = c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055f) / 1.055f, 2.4f);
                }
                noise = new float[n.Length];
                for (int i = 0; i < n.Length; i++)
                    noise[i] = srgb[n[i]];
                BuildGrads();
                planeStart = r.BaseStream.Position;
            }
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("VitaWodTerrain: " + e.Message);
            woods = null;
            return false;
        }
    }

    public static byte[] Woods { get { return woods; } }

    // Mod: TerrainComputer.DispatchAndProcess + kernel TerrainComputer. data[y + x * 129] = height of sample (x, y), 0-1 of 5000.
    public static void Generate(int mpx, int mpy, NativeArray<float> data)
    {
        VitaWodTerrain tile = new VitaWodTerrain();
        tile.Begin(mpx, mpy);
        for (int x = 0; x < Res; x++)
            for (int y = 0; y < Res; y++)
                data[y + x * Res] = tile.Sample(x, y);
    }

    public void Begin(int mpx, int mpy)
    {
        this.mpx = mpx;
        this.mpy = mpy;
        tpx = mpx * 129f;
        tpy = (499 - mpy) * 129f;
        GatherLocations(mpx, mpy, tpx, tpy);
        GatherRoads(mpx, mpy);
        LoadWindow(tpx, tpy);
        Prepare();
    }

    // Per tile, after the location/road/map gathering: coarse grid reset, road segments
    internal void Prepare()
    {
        BuildRoadSegments();
        cn = 128 / CS + 1;
        if (coarseDone.Length != cn * cn)
        {
            coarse = new float[cn * cn * CF];
            coarseDone = new bool[cn * cn];
        }
        else
            Array.Clear(coarseDone, 0, coarseDone.Length);
    }

    // Coarse point values: 0-2 location pass (weight, height, wilderness), 3-4 port pass (weight, height; = pass 1 without a port),
    // 5 road weight, 6 colour alpha, 7 Jordan modulators, 8 dune bump
    void CoarsePoint(int cx, int cy, int o)
    {
        int idx = cx * CS, idy = cy * CS;
        float posx = tpx + idx / 128f * 129f, posy = tpy + idy / 128f * 129f;
        float portU = Saturate(posx / WorldW) + (-0.25f / TerrainX), portV = Saturate(posy / WorldH) + (-1.1f / TerrainY);
        float portLerp = Saturate(SampleMap(1, portU, portV) * 10f);
        float h, b;
        coarse[o] = LocationWeight(posx, posy, portLerp, 64f, out h, out b);
        coarse[o + 1] = h;
        coarse[o + 2] = b;
        if (portLerp == 0f)
        {
            coarse[o + 3] = coarse[o];
            coarse[o + 4] = h;
        }
        else
        {
            coarse[o + 3] = LocationWeight(posx, posy, portLerp, Lerp(64f, 3f, portLerp), out h, out b);
            coarse[o + 4] = h;
        }
        coarse[o + 5] = RoadWeight(posx, posy, tpx, tpy);
        float colorA = ColorAlpha(posx, posy);
        coarse[o + 6] = colorA;
        float plateauWeight = colorA;   // max(colorA, desert) * (1 - mountainBase) with the black biome map
        coarse[o + 7] = JordanModulators(posx, posy, Lerp(JordWarp, 200f, plateauWeight), JordPers, JordDamp, JordMax);
        coarse[o + 8] = PositivePerlin(posx, posy, DuneOct, DuneFreq, DuneAmp, DuneLac, DunePers, DuneOffX, DuneOffY, DuneMax);
        float t = 0f, dx = 0f, dy = 0f, wx = 0f, wy = 0f;
        CellOctaves(posx, posy, 0, CellSplit, ref t, ref dx, ref dy);
        coarse[o + 9] = t;
        coarse[o + 10] = dx;
        coarse[o + 11] = dy;
        t = dx = dy = 0f;
        JordanOctaves(posx, posy, JordFreq, Lerp(JordWarp, 200f, plateauWeight), JordPers, JordDamp, 0, JordSplit, ref t, ref wx, ref wy, ref dx, ref dy);
        coarse[o + 12] = t;
        coarse[o + 13] = wx;
        coarse[o + 14] = wy;
        coarse[o + 15] = dx;
        coarse[o + 16] = dy;
    }

    // Bilinear of the coarse points around sample (idx, idy) into v[0..CF)
    void Coarse(int idx, int idy, float[] v)
    {
        int cx = Math.Min(idx / CS, cn - 2), cy = Math.Min(idy / CS, cn - 2);
        float ax = (idx - cx * CS) / (float)CS, ay = (idy - cy * CS) / (float)CS;
        for (int i = 0; i < 4; i++)
        {
            int c = (cx + (i & 1)) * cn + cy + (i >> 1);
            if (!coarseDone[c])
            {
                CoarsePoint(cx + (i & 1), cy + (i >> 1), c * CF);
                coarseDone[c] = true;
            }
        }
        int c00 = (cx * cn + cy) * CF, c10 = c00 + cn * CF, c01 = c00 + CF, c11 = c10 + CF;
        for (int f = 0; f < CF; f++)
            v[f] = Lerp(Lerp(coarse[c00 + f], coarse[c10 + f], ax), Lerp(coarse[c01 + f], coarse[c11 + f], ax), ay);
    }

    readonly float[] cv = new float[CF];

    public float Sample(int x, int y)
    {
        return Math.Max(100f / NewHeight, HeightSample(x, y, tpx, tpy));
    }

    void GatherLocations(int mpx, int mpy, float tpx, float tpy)
    {
        near.Clear();
        DaggerfallUnity dfu = DaggerfallUnity.Instance;
        // a location 7+ map pixels away is over 774 units from the tile, beyond reach (600 + half diagonal + 7.5)
        for (int x = -6; x <= 6; x++)
            for (int y = -6; y <= 6; y++)
            {
                int px = mpx + x, py = mpy + y;
                if (px < 0 || px >= MapsFile.MaxMapPixelX || py < 0 || py >= MapsFile.MaxMapPixelY)
                    continue;
                int key = px + py * MapsFile.MaxMapPixelX;
                Rect rect;
                if (!rects.TryGetValue(key, out rect))
                {
                    rect = new Rect();
                    ContentReader.MapSummary summary;
                    if (dfu.ContentReader.HasLocation(px, py, out summary))
                    {
                        DFLocation location = dfu.ContentReader.MapFileReader.GetLocation(summary.RegionIndex, summary.MapIndex);
                        Rect r = DaggerfallLocation.GetLocationRect(location);
                        if (r.width != 0 && r.height != 0)
                        {
                            float k = 129f / 32768f;
                            rect = Rect.MinMaxRect(r.xMin * k - 1, r.yMin * k - 1, r.xMax * k + 1, r.yMax * k + 1);
                        }
                    }
                    rects[key] = rect;
                }
                if (rect.width == 0)
                    continue;
                float lx = (float)Math.Floor(rect.xMin), ly = (float)Math.Floor(rect.yMin);
                float sx = (float)Math.Floor(rect.width), sy = (float)Math.Floor(rect.height);
                float cx = lx + sx * 0.5f, cy = ly + sy * 0.5f;
                float reach = 600f + (float)Math.Sqrt(sx * sx + sy * sy) * 0.5f + 7.5f + 92f;   // + tile half diagonal from its centre
                float dx = cx - (tpx + 64.5f), dy = cy - (tpy + 64.5f);
                if (dx * dx + dy * dy < reach * reach)
                    near.Add(new Rect(lx, ly, sx, sy));
            }
    }

    // Mod: BasicRoadsUtils.GetRoadData (roads and tracks of the 3x3 map pixels around), as N,E,S,W,NW,NE,SW,SE flags per map pixel
    void GatherRoads(int mpx, int mpy)
    {
        Array.Clear(roadBits, 0, roadBits.Length);
        byte[] roads = null, tracks = null;
#if VITA_MODS
        roads = DaggerfallWorkshop.BasicRoads.BasicRoadsTexturing.VitaSharedPathData(0);
        tracks = DaggerfallWorkshop.BasicRoads.BasicRoadsTexturing.VitaSharedPathData(1);
#endif
        if (roads == null || tracks == null)
            return;
        byte[] dirs = { 128, 32, 8, 2, 1, 64, 4, 16 };
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            {
                int px = mpx + x, py = mpy + y;
                if (px < 0 || px >= MapsFile.MaxMapPixelX || py < 0 || py >= MapsFile.MaxMapPixelY)
                    continue;
                int i = px + py * MapsFile.MaxMapPixelX, si = (x + 1) + (y + 1) * 3;
                for (int d = 0; d < 8; d++)
                    roadBits[si * 8 + d] = ((roads[i] | tracks[i]) & dirs[d]) != 0 ? 1f : 0f;
            }
    }

    void LoadWindow(float tpx, float tpy)
    {
        // texels any sample of this tile can touch (deriv at worldUv, port/road at portUv), +1 for bilinear neighbours
        float u0 = Math.Min(tpx / WorldW - 0.25f / TerrainX, tpx / WorldW), v0 = Math.Min(tpy / WorldH - 1.1f / TerrainY, tpy / WorldH);
        float u1 = (tpx + 129f) / WorldW, v1 = (tpy + 129f) / WorldH;
        winX = Math.Max(0, (int)Math.Floor(u0 * mapW - 0.5f) - 1);
        winY = Math.Max(0, (int)Math.Floor(v0 * mapH - 0.5f) - 1);
        winW = Math.Min(mapW, (int)Math.Floor(u1 * mapW - 0.5f) + 3) - winX;
        winH = Math.Min(mapH, (int)Math.Floor(v1 * mapH - 0.5f) + 3) - winY;
        byte[] row = new byte[winW];
        using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
            for (int p = 0; p < 5; p++)
            {
                if (win[p] == null || win[p].Length < winW * winH)
                    win[p] = new float[winW * winH];
                for (int y = 0; y < winH; y++)
                {
                    f.Position = planeStart + (long)p * mapW * mapH + (long)(winY + y) * mapW + winX;
                    f.Read(row, 0, winW);
                    for (int x = 0; x < winW; x++)
                        win[p][x + y * winW] = srgb[row[x]];
                }
            }
    }

    // Bilinear, clamped (Unity linear clamp sampler) on a map plane via the window
    float SampleMap(int p, float u, float v)
    {
        float tx = u * mapW - 0.5f, ty = v * mapH - 0.5f;
        float fx = Floor(tx), fy = Floor(ty);
        float ax = tx - fx, ay = ty - fy;
        int x0 = Clamp((int)fx, mapW) - winX, x1 = Clamp((int)fx + 1, mapW) - winX;
        int y0 = Clamp((int)fy, mapH) - winY, y1 = Clamp((int)fy + 1, mapH) - winY;
        float[] w = win[p];
        float a = Lerp(w[x0 + y0 * winW], w[x1 + y0 * winW], ax);
        float b = Lerp(w[x0 + y1 * winW], w[x1 + y1 * winW], ax);
        return Lerp(a, b, ay);
    }

    // mapPixelHeights: the rewritten WOODS map as a 1000x500 linear texture, flipped (ToBasemap)
    static float SampleHeights(float u, float v)
    {
        float tx = u * 1000f - 0.5f, ty = v * 500f - 0.5f;
        float fx = Floor(tx), fy = Floor(ty);
        float ax = tx - fx, ay = ty - fy;
        int x0 = Clamp((int)fx, 1000), x1 = Clamp((int)fx + 1, 1000);
        int y0 = 499 - Clamp((int)fy, 500), y1 = 499 - Clamp((int)fy + 1, 500);
        float a = Lerp(woods[x0 + y0 * 1000] / 255f, woods[x1 + y0 * 1000] / 255f, ax);
        float b = Lerp(woods[x0 + y1 * 1000] / 255f, woods[x1 + y1 * 1000] / 255f, ax);
        return Lerp(a, b, ay);
    }

    static float SampleNoise(float u, float v)
    {
        float tx = u * 128f - 0.5f, ty = v * 128f - 0.5f;
        float fx = Floor(tx), fy = Floor(ty);
        float ax = tx - fx, ay = ty - fy;
        int x0 = Clamp((int)fx, 128), x1 = Clamp((int)fx + 1, 128), y0 = Clamp((int)fy, 128), y1 = Clamp((int)fy + 1, 128);
        float a = Lerp(noise[x0 + y0 * 128], noise[x1 + y0 * 128], ax);
        float b = Lerp(noise[x0 + y1 * 128], noise[x1 + y1 * 128], ax);
        return Lerp(a, b, ay);
    }

    static int Clamp(int i, int n) { return i < 0 ? 0 : (i >= n ? n - 1 : i); }
    static float Lerp(float a, float b, float t) { return a + (b - a) * t; }
    static float Saturate(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
    // Math.Floor is a double library call per use under IL2CPP; every value here is far inside int range
    static float Floor(float v) { int i = (int)v; return v < i ? i - 1 : i; }
    static float Frac(float v) { return v - Floor(v); }
    static float Smoothstep(float a, float b, float x) { float t = Saturate((x - a) / (b - a)); return t * t * (3f - 2f * t); }

    // Mod: GetHeightSample
    float HeightSample(int idx, int idy, float tpx, float tpy)
    {
        float posx = tpx + idx / 128f * 129f, posy = tpy + idy / 128f * 129f;

        float portU = Saturate(posx / WorldW) + (-0.25f / TerrainX), portV = Saturate(posy / WorldH) + (-1.1f / TerrainY);
        float portHeight = SampleMap(1, portU, portV);
        float seaHeight = SampleMap(3, portU, portV);
        float altHeight = SampleMap(2, portU, portV);
        float smoothRoads = SampleMap(4, portU, portV);

        float portLerp = Saturate(portHeight * 10f);
        float[] v = cv;
        Coarse(idx, idy, v);
        float locationHeight, bumpWeight;
        float locationWeight;
        if ((CoarseMask & 1) != 0)
        {
            locationWeight = v[0];
            locationHeight = v[1];
            bumpWeight = v[2];
        }
        else
            locationWeight = LocationWeight(posx, posy, portLerp, 64f, out locationHeight, out bumpWeight);
        float wildernessWeight = bumpWeight;
        float firstLocationWeight = locationWeight, firstLocationHeight = locationHeight;
        bumpWeight *= (float)Math.Sqrt(Math.Sqrt(locationWeight));
        locationHeight = locationHeight * 0.75f;

        float extraHeight, bumps;
        float baseHeight = BaseHeight(posx, posy, 0f, 0f, 0f, v, bumpWeight, out extraHeight, out bumps);

        float roadWeight = (CoarseMask & 2) != 0 ? v[5] : RoadWeight(posx, posy, tpx, tpy);

        locationWeight = Saturate(locationWeight - 0.5f) / 0.5f;

        // the mod passes locationHeight and bumpWeight again as out parameters: the port pass replaces both
        // (no port: the same call as the first one, so reuse its result; bumpWeight isn't read after this)
        float portLocationWeight;
        if ((CoarseMask & 1) != 0)
        {
            portLocationWeight = v[3];
            locationHeight = v[4];
        }
        else if (portLerp == 0f)
        {
            portLocationWeight = firstLocationWeight;
            locationHeight = firstLocationHeight;
        }
        else
            portLocationWeight = LocationWeight(posx, posy, portLerp, Lerp(64f, 3f, portLerp), out locationHeight, out bumpWeight);

        float regLocationHeight = Math.Max(locationHeight + (100f / 10000f) + (altHeight * 0.1f), 0.021f);
        if (seaHeight > 0f)
            locationHeight = 0f;
        else
            locationHeight = Lerp(regLocationHeight, 0.021f, portLerp);

        if (baseHeight <= 0.021f)
        {
            float b1 = Lerp(baseHeight, locationHeight, locationWeight);
            float b2 = Lerp(baseHeight, locationHeight, portLocationWeight);
            baseHeight = Lerp(b1, b2, portLerp);
        }
        else
            baseHeight = Lerp(baseHeight, locationHeight, locationWeight);
        // transition noise: weighted by saturate(((baseHeight - 100) / 5000) / 5), 0 for any 0-1 height (see header)

        extraHeight *= (1f - locationWeight);
        baseHeight += (extraHeight + bumps) * Saturate(1.3f - (roadWeight * smoothRoads));
        return baseHeight;
    }

    // Mod: LocationWeight / PortLocationWeight (they differ only in the base fade distance)
    float LocationWeight(float posx, float posy, float portLerp, float fadeBase, out float basemapHeight, out float wildernessWeight)
    {
        float weight = 0f, narrowWeight = 0f, height = 0f, nearestHeight = 0f, nearestDist = 99999999.9f;
        float offU = -1.5f / TerrainX, offV = 0.5f / TerrainY;
        float fallbackHeight = SampleHeights(Saturate(posx / WorldW) + offU, Saturate(posy / WorldH) + offV);

        for (int i = 0; i < near.Count; i++)
        {
            Rect loc = near[i];
            float lpx = loc.x, lpy = loc.y, lsx = loc.width, lsy = loc.height;
            float lcx = lpx + lsx * 0.5f, lcy = lpy + lsy * 0.5f;
            float dcx = posx - lcx, dcy = posy - lcy;
            float dist = (float)Math.Sqrt(dcx * dcx + dcy * dcy);
            float halfDiag = (float)Math.Sqrt(lsx * lsx * 0.25f + lsy * lsy * 0.25f);   // distance(locCenter, locPos)
            if (dist - (halfDiag + 7.5f) > 600f)
                continue;   // out of reach: weight 0, narrow 0, never the nearest that matters

            float baseHeight = SampleHeights(Saturate(lcx / WorldW) + offU, Saturate(lcy / WorldH) + offV);
            float fadeDist = fadeBase;

            // NearestRectangularEdgePoint
            float rectx = lcx + Math.Max(-lsx * 0.5f, Math.Min(lsx * 0.5f, dcx));
            float recty = lcy + Math.Max(-lsy * 0.5f, Math.Min(lsy * 0.5f, dcy));
            // NearestRadialEdgePoint (its nearestEdgeNarrow, NearestEdgePoint, replaces the rectangular one)
            float narx, nary;
            NearestEdgePoint(posx, posy, lpx, lpy, lsx, lsy, out narx, out nary);
            float n = Saturate(PositivePerlin(narx + 500f, nary + 500f, 8, 0.015f, DuneAmp, DuneLac, 0.7f, DuneOffX, DuneOffY, NewHeight));
            float edgeDist = halfDiag + 7.5f * (n * n);
            float radx, rady;
            if (dist < edgeDist)
            {
                radx = posx;
                rady = posy;
            }
            else
            {
                radx = lcx + dcx / dist * edgeDist;
                rady = lcy + dcy / dist * edgeDist;
            }
            float nex = Lerp(radx, rectx, portLerp), ney = Lerp(rady, recty, portLerp);

            float ndx = posx - narx, ndy = posy - nary;
            float narrowDist = (float)Math.Sqrt(ndx * ndx + ndy * ndy);
            if (narrowDist < 15f)   // maxNarrowDist <= 15: farther adds exactly 0
            {
                n = Saturate(PositivePerlin(narx, nary, 8, 0.05f, 1f, 2f, 0.73f, DuneOffX, DuneOffY, NewHeight));
                float maxNarrowDist = Lerp(5f, 15f, n);
                narrowWeight += 1f - Saturate(narrowDist / maxNarrowDist);
            }

            float edx = nex - posx, edy = ney - posy;
            float eDist = (float)Math.Sqrt(edx * edx + edy * edy);
            float w;
            if (eDist >= fadeDist * 9f)
                w = 0f;     // fadeDist grows to at most 9x its base: exactly 0 past that
            else if (eDist == 0f)
                w = 1f;     // pos on its own edge point (the mod's normalize(0) only feeds a fade that can't matter here)
            else
            {
                float dirx = nex - lcx, diry = ney - lcy;
                float dl = (float)Math.Sqrt(dirx * dirx + diry * diry);
                float spx = nex + dirx / dl * 5f, spy = ney + diry / dl * 5f;
                n = Saturate(PositivePerlin(spx, spy, 16, 0.007f, DuneAmp, DuneLac, 0.73f, DuneOffX, DuneOffY, NewHeight));
                fadeDist += fadeDist * 8f * (n * n);
                w = 1f - Saturate(eDist / fadeDist);
                w = w * w;
                w = Smoothstep(0f, 1f, w);
            }
            weight += w;
            if (eDist < nearestDist)
            {
                nearestHeight = baseHeight;
                nearestDist = eDist;
            }
            height += baseHeight * w;
        }

        wildernessWeight = 1f - Saturate(narrowWeight);
        if (weight == 0f)
        {
            basemapHeight = fallbackHeight;     // the mod's 0/0 here ends as lerp(fallback, x, 0)
            return 0f;
        }
        basemapHeight = Math.Max(80f / NewHeight, height / weight);
        weight = Saturate(weight);
        basemapHeight = Lerp(basemapHeight, nearestHeight, Saturate(narrowWeight));
        basemapHeight = Saturate(basemapHeight);
        basemapHeight = Lerp(fallbackHeight, basemapHeight, weight);
        return weight;
    }

    static void NearestEdgePoint(float px, float py, float lx, float ly, float sx, float sy, out float ex, out float ey)
    {
        float maxx = lx + sx, maxy = ly + sy;
        bool insideY = py > ly && py < maxy, insideX = px > lx && px < maxx;
        ex = px; ey = py;
        if (insideX && insideY)
            return;
        if (insideY)
        {
            ex = Math.Abs(px - lx) < Math.Abs(px - maxx) ? lx : maxx;
            return;
        }
        if (insideX)
        {
            ey = Math.Abs(py - ly) < Math.Abs(py - maxy) ? ly : maxy;
            return;
        }
        float d1 = Dist(px, py, lx, ly), d2 = Dist(px, py, maxx, ly), d3 = Dist(px, py, lx, maxy), d4 = Dist(px, py, maxx, maxy);
        if (d1 < d2 && d1 < d3 && d1 < d4) { ex = lx; ey = ly; }
        else if (d2 < d3 && d2 < d4) { ex = maxx; ey = ly; }
        else if (d3 < d4) { ex = lx; ey = maxy; }
        else { ex = maxx; ey = maxy; }
    }

    static float Dist(float ax, float ay, float bx, float by) { float dx = ax - bx, dy = ay - by; return (float)Math.Sqrt(dx * dx + dy * dy); }

    // Mod: GetBaseHeight, with its detailed base sample always 1 (see header). br/bg/bb = the biome map's texel (0 in 1.5.0); terms whose
    // weight is 0 are skipped (same result: they are only ever multiplied by it).
    float BaseHeight(float posx, float posy, float br, float bg, float bb, float[] v, float bumpWeight, out float extraHeight, out float bumps)
    {
        float worldU = posx / WorldW, worldV = posy / WorldH;
        float db = SampleMap(0, worldU, worldV);
        float loRes = Saturate((db * 255f * (8f + 4f) - 27.2f) / 2308.5f);
        float land = Lerp(1f, loRes, Saturate((1f - 1.5f / 2308.5f) / (100f / 2308.5f)));
        float mountain = Smoothstep(0f, 1f, br), mountainBase = mountain, desert = bg, hills = bb;

        // ColorPerlin (maxHeight 0: power 1); .g/.b only feed the faults (weighted by mountain)
        float gx = 0f + 483.58f, gy = 0f + 195.28f, bx = gx + 116.26f, by = gy + 994.28f, ax = bx + 472.89f, ay = by + 192.57f;
        float f = ColorFreq, amp = ColorAmp, cr = 0f, cg = 0f, cb = 0f, ca = 0f;
        for (int i = 0; i < ColorOct; i++)
        {
            if (mountain > 0f)
            {
                cg += Perlin2D((posx + gx) * f * 0.2f, (posy + gy) * f * 0.2f) * amp;
                cb += Perlin2D((posx + bx) * f * 0.01f, (posy + by) * f * 0.01f) * amp;
            }
            if ((CoarseMask & 4) == 0)
                ca += Perlin2D((posx + ax) * f * 0.002f, (posy + ay) * f * 0.002f) * amp;
            f *= ColorLac;
            amp *= ColorPers;
        }
        float colorG = Saturate(cg * 0.5f + 0.5f), colorB = Saturate(cb * 0.5f + 0.5f);
        float colorA = (CoarseMask & 4) != 0 ? v[6] : Saturate(ca * 0.5f + 0.5f);

        float ct = v[9], cdx = v[10], cdy = v[11];
        CellOctaves(posx, posy, CellSplit, CellOctUse, ref ct, ref cdx, ref cdy);
        float cell = ct * (CellMax / NewHeight);
        float landVar = Math.Abs(cell) * (NewHeight / CellMax);

        float iqMntWeight = Saturate(1f - (mountain / 0.25f));
        float swissMntWeight = Saturate(mountain * (1f / 0.4f) - 0.6f);
        float jordanMntWeight = Saturate((1f - iqMntWeight) * (1f - swissMntWeight));
        mountain = Smoothstep(0.4f, 0.6f, Saturate(mountain / 0.25f));
        hills *= (1f - mountain) * (1f - desert);
        float hillNoiseFactor = Saturate(hills / 0.1f);
        float plateauWeight = Math.Max(colorA, desert) * (1f - mountainBase);

        float baseHeightMax = Lerp(Lerp(800f, 1000f, hills), 1200f, mountain);
        float baseHeight = Lerp((100f - 1f) / NewHeight, baseHeightMax / NewHeight, land);
        float coastlineVar = Lerp(40f, 80f, landVar);
        float beachMin = Saturate(land - Lerp(5f, 15f, landVar) / NewHeight);
        float landWeight = Saturate(beachMin / (Lerp(coastlineVar * 10f, coastlineVar * 20f, landVar) / NewHeight));

        float bump = ((CoarseMask & 16) != 0 ? v[8] : PositivePerlin(posx, posy, DuneOct, DuneFreq, DuneAmp, DuneLac, DunePers, DuneOffX, DuneOffY, DuneMax)) * landWeight * (1f - mountain);
        bumps = bumpWeight > 0f ? SimplePerlin(posx, posy, BumpOctUse, DuneFreq * 64f, DuneAmp, DuneLac, DunePers, DuneOffX, DuneOffY, 6.5f) * landWeight * bumpWeight : 0f;
        float rockyBase = cell * landWeight * mountain * 0.25f;   // SwissCellNoise(SwissCell(pos)): the same noise as landVar's
        float dunes = desert > 0f ? SwissGen(posx, posy, DuneSOct, DuneSFreq, DuneSAmp, DuneSLac, DuneSPers, DuneSOffX, DuneSOffY, DuneSRidge, DuneSWarp, DuneSMax) : 0f;
        float swissMnt = 0f;
        if (swissMntWeight > 0f && landWeight > 0f)
            swissMnt = Saturate(SwissMountains(posx, posy)) * landWeight;

        float jordanMnt = 0f;
        float jordanUse = jordanMntWeight + hills + plateauWeight;
        if (jordanUse > 0f && landWeight > 0f)
        {
            float jw = Lerp(Lerp(JordWarp, 200f, plateauWeight), -JordWarp, hillNoiseFactor), jp = Lerp(JordPers, 0.2f, hillNoiseFactor);
            float jd = Lerp(JordDamp, 1f, hillNoiseFactor), jm = Lerp(JordMax, 800f, hillNoiseFactor);
            float mod = (CoarseMask & 8) != 0 ? v[7] : JordanModulators(posx, posy, jw, jp, jd, jm);
            float js = v[12], jwx = v[13], jwy = v[14], jdx = v[15], jdy = v[16];
            JordanOctaves(posx, posy, JordFreq, jw, jp, jd, JordSplit, JordOctUse, ref js, ref jwx, ref jwy, ref jdx, ref jdy);
            jordanMnt = (js + (JordDampScale - 1f)) * (jm / NewHeight) * mod * landWeight;
        }
        float iqMnt = 0f;
        if (mountain > 0f && landWeight > 0f)
            iqMnt = IQMountains(posx, posy) * landWeight * mountain;
        iqMnt = Lerp(iqMnt, jordanMnt, plateauWeight);

        float mnt = swissMnt * swissMntWeight + jordanMnt * jordanMntWeight + iqMnt * iqMntWeight;
        float hill = jordanMnt * hills + dunes * desert;
        baseHeight += bump + rockyBase;

        float subtraction = 0f;
        if (mountain > 0f)
            subtraction = (1f - SwissTime(posx, posy)) * Saturate((colorG * colorB) * 2f - 1f) * mountain;

        extraHeight = Saturate(mnt + hill) - landVar * (10f / NewHeight);
        extraHeight -= subtraction * Math.Min(extraHeight + (baseHeight - 115f / NewHeight), 500f / NewHeight);
        return baseHeight;
    }

    // Mod: SwissMountainsGen (simplex with derivatives, ridged, warped)
    static float SwissGen(float px, float py, int octaves, float freq, float amp, float lac, float pers, float ox, float oy, float ridge, float warpIn, float maxHeight)
    {
        float f = freq, a = Lerp(amp * 0.125f, amp, ridge), t = 0f, dx = 0f, dy = 0f;
        float warp = Lerp(warpIn * 0.1f, warpIn, ridge);
        float nx, ny, nz;
        for (int i = 0; i < octaves; i++)
        {
            Simplex((px + warp * dx) * f + ox, (py + warp * dy) * f + oy, out nx, out ny, out nz);
            t += a * (1f - Math.Abs(nx));
            dx += a * ny * -nx;
            dy += a * nz * -nx;
            f *= lac;
            a *= pers * Saturate(t);
        }
        return t * (maxHeight / NewHeight);
    }

    // Mod: SwissMountains(SwissFolded(pos))
    static float SwissMountains(float px, float py)
    {
        float w1 = SwissGen(px, py, FoldOct, FoldFreq * 0.5f, FoldAmp, FoldLac, FoldPers, FoldOffX, FoldOffY, FoldRidge, FoldWarp * 0.5f, FoldMax);
        float w2 = SwissGen(px, py, FoldOct, FoldFreq * 0.25f, FoldAmp, FoldLac, FoldPers, FoldOffX, FoldOffY, FoldRidge, FoldWarp * 0.25f, FoldMax);
        w1 = Saturate(w1 * 0.5f + (1f - 0.5f));
        w2 = Saturate(w2 * 0.25f + (1f - 0.25f));
        return SwissGen(px, py, FoldOct, FoldFreq, FoldAmp, FoldLac, FoldPers, FoldOffX, FoldOffY, FoldRidge, FoldWarp, FoldMax) * w1 * w2;
    }

    // Mod: IQMountains(IQMountain(pos))
    static float IQMountains(float px, float py)
    {
        float sum = 0f, dx = 0f, dy = 0f, f = IqFreq, amp = IqAmp;
        float nx, ny, nz;
        for (int i = 0; i < IqOct; i++)
        {
            Surflet((px + dx * IqWarp + IqOffX) * f, (py + dy * IqWarp + IqOffY) * f, out nx, out ny, out nz);
            dx += ny * -nx;
            dy += nz * -nx;
            sum += amp * nx / (1f + dx * dx + dy * dy);
            f *= IqLac;
            amp *= IqPers;
        }
        return Saturate(sum) * (IqMax / NewHeight);
    }

    // Mod: SwissTime(SwissFaults(pos))
    static float SwissTime(float px, float py)
    {
        float sum = 0f, dx = 0f, dy = 0f, f = FaultFreq, amp = FaultAmp;
        float nx, ny, nz;
        for (int i = 0; i < FaultOct; i++)
        {
            Simplex((px + FaultOffX + FaultWarp * dx) * f, (py + FaultOffY + FaultWarp * dy) * f, out nx, out ny, out nz);
            nx = 0.5f * (FaultRidge - Math.Abs((float)Math.Sin(nx)));
            ny = 0.5f * (FaultRidge - Math.Abs((float)Math.Sin(ny)));
            nz = 0.5f * (FaultRidge - Math.Abs((float)Math.Sin(nz)));
            nx *= nx; ny *= ny; nz *= nz;
            sum += amp * nx;
            dx += amp * ny * -nx;
            dy += amp * nz * -nx;
            f *= FaultLac;
            amp *= FaultPers;
        }
        float r = Saturate((1f - sum) * 2f);
        return r * r * r * (FaultMax / NewHeight);
    }

    // Mod: SimplePerlin / PositivePerlin
    static float PerlinSum(float px, float py, int octaves, float freq, float amp, float lac, float pers, float ox, float oy)
    {
        float sum = 0f;
        float x = px + ox, y = py + oy;
        for (int i = 0; i < octaves; i++)
        {
            sum += Perlin2D(x * freq, y * freq) * amp;
            freq *= lac;
            amp *= pers;
        }
        return sum;
    }

    static float SimplePerlin(float px, float py, int octaves, float freq, float amp, float lac, float pers, float ox, float oy, float maxHeight)
    {
        float s = PerlinSum(px, py, octaves, freq, amp, lac, pers, ox, oy);
        return Math.Max(-1f, Math.Min(1f, s)) * (maxHeight / NewHeight);
    }

    static float PositivePerlin(float px, float py, int octaves, float freq, float amp, float lac, float pers, float ox, float oy, float maxHeight)
    {
        return Saturate(PerlinSum(px, py, octaves, freq, amp, lac, pers, ox, oy) * 0.5f + 0.5f) * (maxHeight / NewHeight);
    }

    // Mod: SwissCellNoise(SwissCell(pos)) with amplitude 1 (offset unused there), octaves [from, to) on the running sums t, dx, dy;
    // the result is t * (CellMax / NewHeight)
    static void CellOctaves(float px, float py, int from, int to, ref float t, ref float dx, ref float dy)
    {
        float f = CellFreq;
        float a = Lerp(1f * 0.125f, 1f, CellRidge);
        for (int i = 0; i < from; i++)
        {
            f *= CellLac;
            a *= CellPers;
        }
        float warp = Lerp(CellWarp * 0.1f, CellWarp, CellRidge);
        float nx, ny, nz;
        for (int i = from; i < to; i++)
        {
            Surflet((px + warp * dx) * f, (py + warp * dy) * f, out nx, out ny, out nz);
            t += a * (CellRidge - Math.Abs(nx));
            dx += a * ((ny - (1f - CellRidge)) / CellRidge) * -nx;
            dy += a * ((nz - (1f - CellRidge)) / CellRidge) * -nx;
            f *= CellLac;
            a *= CellPers;
        }
    }

    // Mod: JordanMountains = JordanGen(JordFreq) * these
    static float JordanModulators(float px, float py, float warp, float pers, float damp, float maxHeight)
    {
        float w1 = JordanGen(px, py, JordFreq * 0.25f, warp, pers, damp, maxHeight);
        float w2 = JordanGen(px, py, JordFreq * 0.125f, warp, pers, damp, maxHeight);
        w1 = Saturate(w1 * 0.25f + (1f - 0.25f));
        w2 = Saturate(w2 * 0.125f + (1f - 0.125f));
        return w1 * w2;
    }

    // ColorPerlin's alpha (the plateau weight), the only channel the black biome map leaves in use
    static float ColorAlpha(float px, float py)
    {
        float ax = 483.58f + 116.26f + 472.89f, ay = 195.28f + 994.28f + 192.57f, f = ColorFreq, amp = ColorAmp, ca = 0f;
        for (int i = 0; i < ColorOct; i++)
        {
            ca += Perlin2D((px + ax) * f * 0.002f, (py + ay) * f * 0.002f) * amp;
            f *= ColorLac;
            amp *= ColorPers;
        }
        return Saturate(ca * 0.5f + 0.5f);
    }

    static float JordanGen(float px, float py, float frequency, float warp, float pers, float damp, float maxHeight)
    {
        float sum = 0f, wx = 0f, wy = 0f, dx = 0f, dy = 0f;
        JordanOctaves(px, py, frequency, warp, pers, damp, 0, JordOct, ref sum, ref wx, ref wy, ref dx, ref dy);
        return (sum + (JordDampScale - 1f)) * (maxHeight / NewHeight);
    }

    // Mod: JordanTurbulence's octaves [from, to) on the running sums (sum, warp wx/wy, damping dx/dy)
    static void JordanOctaves(float px, float py, float frequency, float warp, float pers, float damp, int from, int to,
        ref float sum, ref float wx, ref float wy, ref float dx, ref float dy)
    {
        if (to <= from)
            return;
        float nx, ny, nz;
        if (from == 0)
        {
            Surflet(px * frequency, py * frequency, out nx, out ny, out nz);
            sum = nx * nx;
            wx = JordWarp0 * ny * nx;
            wy = JordWarp0 * nz * nx;
            dx = JordDamp0 * ny * nx;
            dy = JordDamp0 * nz * nx;
            from = 1;
        }
        // amp, freq and damped as the loop leaves them after octave from - 1
        float amp = JordPers1 * JordAmp, freq = JordLac, damped = amp * pers;
        for (int i = 1; i < from; i++)
        {
            freq *= JordLac;
            amp *= pers;
            damped = amp * (1f - JordDampScale / (1f + dx * dx + dy * dy));
        }
        for (int i = from; i < to; i++)
        {
            Surflet((px * freq + wx + JordOffX) * frequency, (py * freq + wy + JordOffY) * frequency, out nx, out ny, out nz);
            sum += damped * nx * nx;
            wx += warp * ny * nx;
            wy += warp * nz * nx;
            dx += damp * ny * nx;
            dy += damp * nz * nx;
            freq *= JordLac;
            amp *= pers;
            damped = amp * (1f - JordDampScale / (1f + dx * dx + dy * dy));
        }
    }

    // Road segments of the 3x3 map pixels (start x/y, unit direction x/y, length), grouped per map pixel
    readonly float[] segs = new float[9 * 8 * 5];
    readonly int[] segCount = new int[9];

    void BuildRoadSegments()
    {
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            {
                int si = (x + 1) + (y + 1) * 3, n = 0;
                float tx = tpx + x * 129f, ty = tpy - y * 129f;
                for (int d = 0; d < 8; d++)
                {
                    if (roadBits[si * 8 + d] == 0f)
                        continue;
                    float sx, sy;
                    switch (d)
                    {
                        case 0: sx = 64.5f; sy = 129f; break;   // N
                        case 1: sx = 129f; sy = 64.5f; break;   // E
                        case 2: sx = 64.5f; sy = 0f; break;     // S
                        case 3: sx = 0f; sy = 64.5f; break;     // W
                        case 4: sx = 0f; sy = 129f; break;      // NW
                        case 5: sx = 129f; sy = 129f; break;    // NE
                        case 6: sx = 0f; sy = 0f; break;        // SW
                        default: sx = 129f; sy = 0f; break;     // SE
                    }
                    float ax = tx + sx, ay = ty + sy, lx = tx + 64.5f - ax, ly = ty + 64.5f - ay;
                    float len = (float)Math.Sqrt(lx * lx + ly * ly);
                    int o = (si * 8 + n++) * 5;
                    segs[o] = ax;
                    segs[o + 1] = ay;
                    segs[o + 2] = lx / len;
                    segs[o + 3] = ly / len;
                    segs[o + 4] = len;
                }
                segCount[si] = n;
            }
    }

    // Mod: GetRoadWeight (basicRoads.cginc). Stops adding once a sum saturates (same result).
    float RoadWeight(float posx, float posy, float tpx, float tpy)
    {
        float weight = 0f;
        for (int si = 0; si < 9; si++)
        {
            float w = 0f;
            for (int k = 0, o = si * 40; k < segCount[si] && w < 1f; k++, o += 5)
                w += RoadSegmentWeight(posx, posy, segs[o], segs[o + 1], segs[o + 2], segs[o + 3], segs[o + 4]);
            weight += Saturate(w);
            if (weight >= 1f)
                return 1f;
        }
        return weight;
    }

    static float RoadSegmentWeight(float px, float py, float ax, float ay, float lx, float ly, float len)
    {
        float d = Math.Max(0f, Math.Min(len, (px - ax) * lx + (py - ay) * ly));
        float rx = ax + lx * d, ry = ay + ly * d;
        float dx = px - rx, dy = py - ry;
        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
        if (dist >= 0.77f * 256f)
            return 0f;      // fadeDist <= 256 (noise <= 1): smoothstep of a value <= 0
        float sx = rx, sy = ry;
        if (dist > 0f)
        {
            sx += dx / dist * 15f;
            sy += dy / dist * 15f;
        }
        sx -= Floor(sx / 129f) * 129f;
        sy -= Floor(sy / 129f) * 129f;
        float n = SampleNoise(sx / 129f, sy / 129f);
        float fadeDist = 128f + 128f * 1f * n;
        float w = Smoothstep(0f, 1f, 0.77f - Saturate(dist / fadeDist));
        return w * w;
    }

    // FAST32 hash (2 numbers per cell corner) + classic Perlin, as noises.cginc. The hash of a corner depends only on its coordinates
    // modulo 71 (computed in float as the shader does), so its normalized gradient comes from a table built once with the same float maths.
    const int GN = 88, GO = 8;
    static readonly float[] gradX = new float[GN * GN], gradY = new float[GN * GN];

    static void BuildGrads()
    {
        const float ia = 1f / 951.135664f, ib = 1f / 642.949883f;
        for (int i = 0; i < GN; i++)
            for (int j = 0; j < GN; j++)
            {
                float x = (i - GO) + 26f, y = (j - GO) + 161f;
                x *= x;
                y *= y;
                float c = x * y;
                float gx = Frac(c * ia) - 0.49999f, gy = Frac(c * ib) - 0.49999f;
                float norm = 1f / (float)Math.Sqrt(gx * gx + gy * gy);
                gradX[i * GN + j] = gx * norm;
                gradY[i * GN + j] = gy * norm;
            }
    }

    // Hash's "ix - floor(ix / 71) * 71" (float: can land on -1 or 71 for big ix, hence the table margin GO)
    static int Wrap(float i) { const float inv = 1f / 71f; return (int)(i - Floor(i * inv) * 71f) + GO; }

    static float Perlin2D(float px, float py)
    {
        float ix = Floor(px), iy = Floor(py);
        float fx = px - ix, fy = py - iy, fx1 = px - (ix + 1f), fy1 = py - (iy + 1f);
        int x0 = Wrap(ix) * GN, x1 = Wrap(ix + 1f) * GN, y0 = Wrap(iy), y1 = Wrap(iy + 1f);
        const float s = 1.4142135623730950488f;
        float r0 = (gradX[x0 + y0] * fx + gradY[x0 + y0] * fy) * s;
        float r1 = (gradX[x1 + y0] * fx1 + gradY[x1 + y0] * fy) * s;
        float r2 = (gradX[x0 + y1] * fx + gradY[x0 + y1] * fy1) * s;
        float r3 = (gradX[x1 + y1] * fx1 + gradY[x1 + y1] * fy1) * s;
        float bx = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f), by = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);
        float a = Lerp(r0, r2, by), b = Lerp(r1, r3, by);
        return Lerp(a, b, bx);
    }

    static void Surflet(float px, float py, out float val, out float dxOut, out float dyOut)
    {
        float ix = Floor(px), iy = Floor(py);
        float fx = px - ix, fy = py - iy, fx1 = px - (ix + 1f), fy1 = py - (iy + 1f);
        int x0 = Wrap(ix) * GN, x1 = Wrap(ix + 1f) * GN, y0 = Wrap(iy), y1 = Wrap(iy + 1f);
        float val0 = 0f, dx = 0f, dy = 0f;
        SurfletCorner(x0 + y0, fx, fy, ref val0, ref dx, ref dy);
        SurfletCorner(x1 + y0, fx1, fy, ref val0, ref dx, ref dy);
        SurfletCorner(x0 + y1, fx, fy1, ref val0, ref dx, ref dy);
        SurfletCorner(x1 + y1, fx1, fy1, ref val0, ref dx, ref dy);
        const float k = 2.3703703703703703703703703703704f;
        val = val0 * k;
        dxOut = dx * k;
        dyOut = dy * k;
    }

    static void SurfletCorner(int c, float vx, float vy, ref float val, ref float dx, ref float dy)
    {
        float m = 1f - (vx * vx + vy * vy);
        if (m <= 0f)
            return;     // max(m, 0) = 0: adds nothing
        float gx = gradX[c], gy = gradY[c];
        float g = gx * vx + gy * vy;
        float m2 = m * m, m3 = m * m2;
        float t = -6f * m2 * g;
        val += m3 * g;
        dx += t * vx + m3 * gx;
        dy += t * vy + m3 * gy;
    }

    // Mod: SimplexPerlin2D_Deriv
    static void Simplex(float px, float py, out float val, out float dxOut, out float dyOut)
    {
        const float skew = 0.36602540378443864676372317075294f, unskew = 0.21132486540518711774542560974902f, triH = 0.70710678118654752440084436210485f;
        const float spx = 1f - unskew, spy = -unskew, spz = 1f - 2f * unskew;
        px *= triH;
        py *= triH;
        float d = (px + py) * skew;
        float ix = Floor(px + d), iy = Floor(py + d);
        int x0 = Wrap(ix) * GN, x1 = Wrap(ix + 1f) * GN, y0 = Wrap(iy), y1 = Wrap(iy + 1f);
        float u = (ix + iy) * unskew;
        float v0x = ix - u - px, v0y = iy - u - py;
        float v1x, v1y;
        int c1;
        if (v0x < v0y) { v1x = spx; v1y = spy; c1 = x1 + y0; }
        else { v1x = spy; v1y = spx; c1 = x0 + y1; }
        v1x += v0x; v1y += v0y;
        float v2x = spz + v0x, v2y = spz + v0y;
        float val0 = 0f, dx = 0f, dy = 0f;
        SimplexCorner(x0 + y0, v0x, v0y, ref val0, ref dx, ref dy);
        SimplexCorner(c1, v1x, v1y, ref val0, ref dx, ref dy);
        SimplexCorner(x1 + y1, v2x, v2y, ref val0, ref dx, ref dy);
        const float k = 99.204334582718712976990005025589f;
        val = val0 * k;
        dxOut = dx * k;
        dyOut = dy * k;
    }

    static void SimplexCorner(int c, float vx, float vy, ref float val, ref float dx, ref float dy)
    {
        float m = 0.5f - (vx * vx + vy * vy);
        if (m <= 0f)
            return;
        float gx = gradX[c], gy = gradY[c];
        float g = gx * vx + gy * vy;
        float m2 = m * m, m4 = m2 * m2;
        float t = 8f * m2 * m * g;
        val += m4 * g;
        dx += t * vx - m4 * gx;
        dy += t * vy - m4 * gy;
    }
}
#endif
