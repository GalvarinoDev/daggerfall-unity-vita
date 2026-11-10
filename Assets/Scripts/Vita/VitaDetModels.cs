// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Daggerfall Expanded Textures and RMB Resource Pack models, Vita-native (data from vita/tools/detpack.py in
// StreamingAssets/VitaNative). On the PC DFU instantiates each DET prefab as its own GameObject (thousands of chimneys per city); here
// MeshReader.GetModelData returns them as ModelData, so RMBLayout's combiner merges them into the block mesh like vanilla models and the
// block baker bakes them. Their textures are palette records in TEXTURE.9000 (DET's archive 10000), TEXTURE.9001 (the same in winter)
// and TEXTURE.9002 (DET's named textures), read by TextureFile from VitaNative, so they take the vanilla palette/atlas path.
// ClimateSwaps picks 9001 in winter where DFU itself shows winter (Temperate, Mountain; DET's tables also swap in "desert", which
// DFU never shows in winter).
// Merged models get the block's MeshCollider; on the PC only DET prefabs with a collider block the player (banners/lines don't).
// DET's banners (45179-45182) pick their textures by region (DETCustomRegionMaterials): their submeshes carry key (RegionArchive, slot) and
// ApplyRegion resolves it from det_regions.bin for the player's current region and season, at the same moment DFU builds or re-climates
// the materials (Daggerfall's gate showed the prefab's yellow banners where the PC shows the region's green dragon).
// RMB Resource Pack prefabs with RMBRocksMaterials (ports, used by World of Daggerfall's harbour blocks) pick textures for the player's
// climate and season from CustomRuntimeMaterials/<id>.json: key (ClimateArchive, slot), resolved by ApplyClimateTable from crm_climate.bin
// (vita/tools/rrppack.py) with RMBRocksMaterials' own rules.

#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;

public static class VitaDetModels
{
    public const int Archive = 9000, WinterArchive = 9001, NamedArchive = 9002, RegionArchive = 9006, ClimateArchive = 9007;

    // Model id -> where its record sits; decoded per request, and MeshReader caches it for the level (decoding every model at
    // first use held 4.5 MB / 2,293 objects of GC heap for the whole session)
    struct Entry { public int File, Offset, Length; }
    static Dictionary<uint, Entry> models;
    static readonly List<string> files = new List<string>();
    static readonly List<VitaPack> packs = new List<VitaPack>();
    static Dictionary<string, int> regionIndex;
    static int[] regionSlots;   // per slot: prefab archive, record, then per region: default archive, record, winter archive, record
    static int regionStride;

    public static string Folder
    {
        get { return Path.Combine(Application.streamingAssetsPath, "VitaNative"); }
    }

    /// <summary>A DET model as ModelData, or false for every other id.</summary>
    public static bool TryGetModelData(uint id, out ModelData modelData)
    {
        if (models == null)
            Load();
        // Later packs first: the same "later file wins" order as the .bin index
        // Packs win over .bin files whatever their order; only a build missing some .pak files mixes the two
        for (int i = packs.Count - 1; i >= 0; i--)
            if (packs[i].Contains(id))
                return VitaModelPack.TryRead(packs[i], id, out modelData);
        Entry e;
        if (!models.TryGetValue(id, out e))
        {
            modelData = new ModelData();
            return false;
        }
        byte[] bytes = new byte[e.Length];
        using (FileStream f = new FileStream(files[e.File], FileMode.Open, FileAccess.Read, FileShare.Read, 1))
        {
            f.Position = e.Offset;
            int got = 0;
            while (got < bytes.Length)
            {
                int n = f.Read(bytes, got, bytes.Length - got);
                if (n <= 0)
                    throw new EndOfStreamException(files[e.File]);
                got += n;
            }
        }
        using (BinaryReader r = new BinaryReader(new MemoryStream(bytes)))
            modelData = Decode(r);
        return true;
    }

    /// <summary>Path of a Vita-native TEXTURE file (TextureFile.Load), or null for Daggerfall's own (TEXTURE.000-511). Archives from 512 up
    /// are the port's: DET (9000-9002), New Paintings (9003), DIEP (9004), RRP hills (9005) and mod flats (vita/tools/flatpack.py: 540, 1200, 10010, ...).</summary>
    public static string OverrideFolder;    // editor bake (VitaTextureBaker VITA_BAKE_ONLY): VTEX archives that stand in for TEXTURE.000-511

    public static string TexturePath(string fileName)
    {
        int archive;
        if (fileName.StartsWith("TEXTURE.") && int.TryParse(fileName.Substring(8), out archive) && archive >= 512)
            return Path.Combine(Folder, fileName);
        if (OverrideFolder != null && File.Exists(Path.Combine(OverrideFolder, fileName)))
            return Path.Combine(OverrideFolder, fileName);
        return null;
    }

    /// <summary>ClimateSwaps for the DET archive: its winter records where DFU shows winter.</summary>
    public static int ApplyClimate(ClimateBases climate, ClimateSeason season)
    {
        return season == ClimateSeason.Winter && (climate == ClimateBases.Temperate || climate == ClimateBases.Mountain) ? WinterArchive : Archive;
    }

    /// <summary>A banner slot (RegionArchive) to the texture DETCustomRegionMaterials gives it: the table for PlayerGPS.CurrentRegionName,
    /// its winter list when the season is Winter, no climate swap; the prefab's own texture outside the table (or without a game).</summary>
    public static void ApplyRegion(ref int archive, ref int record)
    {
        if (archive == ClimateArchive)
        {
            ApplyClimateTable(ref archive, ref record);
            return;
        }
        if (archive != RegionArchive)
            return;
        if (regionSlots == null)
            LoadRegions();
        int slot = record * regionStride;
        if (slot < 0 || slot + regionStride > regionSlots.Length)
        {
            archive = NamedArchive;
            record = 0;
            return;
        }
        archive = regionSlots[slot];
        record = regionSlots[slot + 1];
        int region;
        if (GameManager.HasInstance && GameManager.Instance.PlayerGPS && regionIndex.TryGetValue(GameManager.Instance.PlayerGPS.CurrentRegionName ?? "", out region))
        {
            bool winter = DaggerfallUnity.Instance.WorldTime.Now.SeasonValue == DaggerfallDateTime.Seasons.Winter;
            int i = slot + 2 + region * 4 + (winter ? 2 : 0);
            archive = regionSlots[i];
            record = regionSlots[i + 1];
        }
    }

    // crm_climate.bin: per slot the prefab's own archive, record, then per table key (order below) default archive, record, winter archive,
    // record; -1 = the key's list is empty, -2 = the list is shorter than the slot
    const int CrmOcean = 0, CrmDesert = 1, CrmDesert2 = 2, CrmMountain = 3, CrmRainforest = 4, CrmSwamp = 5, CrmSubtropical = 6,
        CrmMountainWoods = 7, CrmWoodlands = 8, CrmHaunted = 9, CrmBalfiera = 10, CrmHammerfell = 11;
    static int[] climateSlots;
    static int climateStride;
    static readonly Dictionary<int, bool> archiveOnVita = new Dictionary<int, bool>();
    static readonly string[] hammerfellRegions = { "Alik'r Desert", "Dragontail Mountains", "Dak'fron", "Lainlyn", "Tigonus", "Ephesus", "Santaki" };

    /// <summary>A climate-table slot (ClimateArchive) to the texture RMBRocksMaterials gives it for the player's climate, region and season; the
    /// prefab's own texture when the table has none or names an archive the Vita doesn't have.</summary>
    static void ApplyClimateTable(ref int archive, ref int record)
    {
        if (climateSlots == null)
            LoadClimateTable();
        int slot = record * climateStride;
        if (slot < 0 || slot + climateStride > climateSlots.Length)
        {
            archive = NamedArchive;
            record = 0;
            return;
        }
        archive = climateSlots[slot];
        record = climateSlots[slot + 1];
        if (!GameManager.HasInstance || !GameManager.Instance.PlayerGPS)
            return;
        PlayerGPS gps = GameManager.Instance.PlayerGPS;
        MapsFile.Climates c = (MapsFile.Climates)gps.CurrentClimateIndex;
        bool winter = DaggerfallUnity.Instance.WorldTime.Now.SeasonValue == DaggerfallDateTime.Seasons.Winter
            && c != MapsFile.Climates.Desert && c != MapsFile.Climates.Desert2 && c != MapsFile.Climates.Subtropical
            && !ClimateSwaps.VitaSnowless((int)c);
        int i = slot + 2 + TableKey(slot, c, gps.CurrentRegionName) * 4 + (winter ? 2 : 0);
        if (climateSlots[i] >= 0 && OnVita(climateSlots[i]))
        {
            archive = climateSlots[i];
            record = climateSlots[i + 1];
        }
    }

    // RMBRocksMaterials.GetMaterialsForClimate + its mountain region rules, which depend on World of Daggerfall - Biomes
    static int TableKey(int slot, MapsFile.Climates c, string region)
    {
        switch (c)
        {
            case MapsFile.Climates.Desert: return CrmDesert;
            case MapsFile.Climates.Desert2: return Has(slot, CrmDesert2) ? CrmDesert2 : CrmDesert;
            case MapsFile.Climates.Mountain:
                if (region == "Isle of Balfiera" && (Has(slot, CrmBalfiera) || climateSlots[slot + 2 + CrmBalfiera * 4 + 2] != -1))
                    return CrmBalfiera;
                if (System.Array.IndexOf(hammerfellRegions, region) >= 0)
                {
                    int set = DaggerfallUnity.Settings.VitaWorldOfDaggerfallBiomes ? CrmHammerfell : CrmBalfiera;
                    if (Has(slot, set) || climateSlots[slot + 2 + set * 4 + 2] != -1)
                        return set;
                }
                return CrmMountain;
            case MapsFile.Climates.Rainforest: return CrmRainforest;
            case MapsFile.Climates.Swamp: return Has(slot, CrmSwamp) ? CrmSwamp : CrmRainforest;
            case MapsFile.Climates.Subtropical: return Has(slot, CrmSubtropical) ? CrmSubtropical : CrmDesert;
            case MapsFile.Climates.MountainWoods: return Has(slot, CrmMountainWoods) ? CrmMountainWoods : CrmMountain;
            case MapsFile.Climates.HauntedWoodlands:
            case MapsFile.Climates.Ocean: return Has(slot, CrmHaunted) ? CrmHaunted : CrmWoodlands;
            default: return CrmWoodlands;
        }
    }

    // The key's default list is not empty (RMBRocksMaterials: defaultMaterials.Length > 0)
    static bool Has(int slot, int key)
    {
        return climateSlots[slot + 2 + key * 4] != -1;
    }

    static bool OnVita(int archive)
    {
        if (archive < 512)
            return true;
        bool on;
        if (!archiveOnVita.TryGetValue(archive, out on))
            archiveOnVita[archive] = on = DaggerfallConnect.Arena2.TextureFile.VitaPackHasArchive(Folder, archive) || File.Exists(Path.Combine(Folder, "TEXTURE." + archive));
        return on;
    }

    static void LoadClimateTable()
    {
        climateSlots = new int[0];
        climateStride = 2;
        try
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(File.ReadAllBytes(Path.Combine(Folder, "crm_climate.bin")))))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "VCRM" || r.ReadInt32() != 1)
                    throw new IOException("not a version 1 file");
                int keys = r.ReadInt32(), slots = r.ReadInt32();
                climateStride = 2 + keys * 4;
                climateSlots = new int[slots * climateStride];
                for (int i = 0; i < climateSlots.Length; i++)
                    climateSlots[i] = r.ReadInt32();
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarningFormat("VitaDetModels: crm_climate.bin not loaded: {0}", e.Message);
        }
    }

    static void LoadRegions()
    {
        regionIndex = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        regionSlots = new int[0];
        regionStride = 2;
        try
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(File.ReadAllBytes(Path.Combine(Folder, "det_regions.bin")))))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "VDRG" || r.ReadInt32() != 1)
                    throw new IOException("not a version 1 file");
                int regions = r.ReadInt32();
                for (int i = 0; i < regions; i++)
                    regionIndex[Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()))] = i;
                int slots = r.ReadInt32();
                regionStride = 2 + regions * 4;
                regionSlots = new int[slots * regionStride];
                for (int i = 0; i < regionSlots.Length; i++)
                    regionSlots[i] = r.ReadInt32();
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarningFormat("VitaDetModels: det_regions.bin not loaded: {0}", e.Message);
        }
    }

    static void Load()
    {
        models = new Dictionary<uint, Entry>();
        // Each mod's models only while its Mods-window switch is on (only Cities/Villages/FDA/FMR data places them).
        // The editor bakers (not playing) always get all of them.
        SettingsManager s = DaggerfallUnity.Settings;
        bool all = !Application.isPlaying;
        if (all || s.VitaExpandedTextures)
            Load(Path.Combine(Folder, "det_models.bin"));
        if (all || s.VitaRmbResourcePack)
            Load(Path.Combine(Folder, "rrp_models.bin"));
        if (all || s.VitaNewPaintings)
            Load(Path.Combine(Folder, "paintings_models.bin"));
        if (all || s.VitaDiep)
            Load(Path.Combine(Folder, "diep_models.bin"));
        if (all || s.VitaRmbResourcePack)
            Load(Path.Combine(Folder, "rrphills_models.bin"));
        if (all || s.VitaWorldOfDaggerfall)
            Load(Path.Combine(Folder, "wod_models.bin"));
    }

    // det_models.bin (DET, vita/tools/detpack.py), rrp_models.bin (RMB Resource Pack, Assets/Editor/Vita/VitaRrpBaker.cs),
    // paintings_models.bin / diep_models.bin (New Paintings / DIEP - Rosy's Resources: detpack.py --named, textures TEXTURE.9003 / 9004),
    // rrphills_models.bin (RMB Resource Pack hills + dome from its .dfmod, TEXTURE.9005)
    static void Load(string path)
    {
        // The same models as a VMDL pack (vita/tools/detvmdl.py + lz4pack.py): one native read + memcpy decode per model, the vanilla
        // models.pak way. The editor bakers (VitaPack is empty there) and a build without the pack read the .bin below.
        VitaPack pack = new VitaPack(Path.ChangeExtension(path, ".pak"), VitaModelPack.Magic);
        if (pack.Count > 0)
        {
            packs.Add(pack);
            return;
        }
        try
        {
            // Index only: header fields per record, skipping the arrays (no whole-file buffer)
            using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
            using (BinaryReader r = new BinaryReader(f))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "VDTM" || r.ReadInt32() != 1)
                    throw new IOException("not a version 1 file");
                int file = files.Count;
                files.Add(path);
                int count = r.ReadInt32();
                for (int m = 0; m < count; m++)
                {
                    long start = f.Position;
                    uint id = r.ReadUInt32();
                    f.Position += 12;
                    int vertices = r.ReadInt32();
                    f.Position += vertices * 32L;
                    int subMeshes = r.ReadInt32();
                    for (int s = 0; s < subMeshes; s++)
                    {
                        f.Position += 8;
                        int n = r.ReadInt32();
                        f.Position += n * 4L;
                    }
                    Entry e = new Entry();
                    e.File = file;
                    e.Offset = (int)start;
                    e.Length = (int)(f.Position - start);
                    models[id] = e;
                }
                if (f.Position != f.Length)
                    throw new IOException("trailing bytes");
            }
            Debug.LogFormat("VitaDetModels: {0} models after {1}", models.Count, Path.GetFileName(path));
        }
        catch (System.Exception e)
        {
            Debug.LogWarningFormat("VitaDetModels: {0} not loaded: {1}", path, e.Message);
        }
    }

    static ModelData Decode(BinaryReader r)
    {
        ModelData data = new ModelData();
        uint id = r.ReadUInt32();
        data.DFMesh.ObjectId = (int)id;
        data.DFMesh.Size.X = r.ReadSingle();
        data.DFMesh.Size.Y = r.ReadSingle();
        data.DFMesh.Size.Z = r.ReadSingle();
        int vertices = r.ReadInt32();
        data.Vertices = new Vector3[vertices];
        data.Normals = new Vector3[vertices];
        data.UVs = new Vector2[vertices];
        for (int i = 0; i < vertices; i++)
            data.Vertices[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        for (int i = 0; i < vertices; i++)
            data.Normals[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        for (int i = 0; i < vertices; i++)
            data.UVs[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
        data.SubMeshes = new ModelData.SubMeshData[r.ReadInt32()];
        List<int> indices = new List<int>();
        for (int s = 0; s < data.SubMeshes.Length; s++)
        {
            data.SubMeshes[s].TextureArchive = r.ReadInt32();
            data.SubMeshes[s].TextureRecord = r.ReadInt32();
            int n = r.ReadInt32();
            data.SubMeshes[s].StartIndex = indices.Count;
            data.SubMeshes[s].PrimitiveCount = n / 3;
            for (int i = 0; i < n; i++)
                indices.Add(r.ReadInt32());
        }
        data.Indices = indices.ToArray();
        return data;
    }
}
#endif
