// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Built-in world-data mods: Beautiful Cities and Beautiful Villages of Daggerfall (carademono; PC versions 0.5.0 / 1.4.2).
// The Vita can't load .dfmod files, and loose WorldData JSON would cost a ~90 ms file lookup each in a folder of thousands, so
// vita/tools/worldpack.py packs each mod into StreamingAssets/VitaMods/<name>.pak (index + deflated minified JSON, objects whose
// models/flats the Vita lacks left out). WorldDataReplacement asks TryGet with DFU's own file names (location-R-L.json,
// BLOCK.RMB.json, BLOCK-I-buildingN.json) before its loose-file and mod lookups. Where packs share a file, the first pack in Open wins:
// the PC load order, last-loaded first (DFU asks the last-loaded mod first; see Open).
// Everything comes as binary (TryGetBlock / TryGetBuilding / TryGetLocation): FullSerializer took 1.5-4 s per Beautiful Cities block on the
// Vita (wpon2), and locations are read while the world streams in.
// settings.ini [Video] VitaBeautifulCities / VitaBeautifulVillages (Mods window); WorldDataReplacement caches what it found, so a
// change applies from the next game start.

#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop.Utility.AssetInjection;

public static class VitaWorldPack
{
    // A struct, not an int[] per entry (19k objects for the GC to mark)
    struct Entry
    {
        public int kind, offset, deflated, raw;
        public int nameStart, nameLength;           // in Pack.names
    }

    // Names in one char[] and the index keyed by a 64-bit hash of the name, not a string per entry (9,483 objects); each hit is
    // checked against the stored name, so lookups stay exact. Two different names with the same hash (never seen) go to overflow.
    class Pack
    {
        public string path;
        public char[] names = new char[4096];
        public int namesUsed;
        public Dictionary<long, Entry> index = new Dictionary<long, Entry>();   // name -> kind (0 JSON, 1 RMB block binary, 2 building binary, 3 location binary, 4 raw bytes), offset, deflated length, data length
        public Dictionary<string, Entry> overflow;

        public int Count
        {
            get { return index.Count + (overflow != null ? overflow.Count : 0); }
        }

        static long Hash(string s)
        {
            ulong h = 14695981039346656037;
            for (int i = 0; i < s.Length; i++)
                h = (h ^ s[i]) * 1099511628211;
            return (long)h;
        }

        bool NameIs(Entry e, string name)
        {
            if (e.nameLength != name.Length)
                return false;
            for (int i = 0; i < name.Length; i++)
                if (names[e.nameStart + i] != name[i])
                    return false;
            return true;
        }

        public void Set(string name, Entry e)       // later entries of the same name win, as with index[name] = entry
        {
            if (namesUsed + name.Length > names.Length)
                System.Array.Resize(ref names, System.Math.Max(names.Length * 2, namesUsed + name.Length));
            name.CopyTo(0, names, namesUsed, name.Length);
            e.nameStart = namesUsed;
            e.nameLength = name.Length;
            namesUsed += name.Length;
            long h = Hash(name);
            Entry old;
            if (index.TryGetValue(h, out old) && !NameIs(old, name))
            {
                if (overflow == null)
                    overflow = new Dictionary<string, Entry>();
                overflow[name] = e;
            }
            else
                index[h] = e;
        }

        public bool TryGetValue(string name, out Entry e)
        {
            if (overflow != null && overflow.TryGetValue(name, out e))
                return true;
            return index.TryGetValue(Hash(name), out e) && NameIs(e, name);
        }

        public IEnumerable<KeyValuePair<string, Entry>> All()
        {
            foreach (Entry e in index.Values)
                yield return new KeyValuePair<string, Entry>(new string(names, e.nameStart, e.nameLength), e);
            if (overflow != null)
                foreach (KeyValuePair<string, Entry> e in overflow)
                    yield return e;
        }
    }

    static List<Pack> packs;

    /// <summary>A block of the packs, ready to use (DFBlock as WorldDataReplacement gets it from JSON, Index still to set).</summary>
    public static bool TryGetBlock(string fileName, out DFBlock block)
    {
        block = new DFBlock();
        byte[] raw = Find(fileName, 1);
        if (raw == null)
            return false;
        using (BinaryReader reader = new BinaryReader(new MemoryStream(raw)))
        {
            block = ReadBlock(reader);
            CheckEnd(reader, fileName);
        }
        return true;
    }

    /// <summary>A building replacement of the packs (BLOCK.RMB-N-buildingM.json), as binary like the blocks.</summary>
    public static bool TryGetBuilding(string fileName, out BuildingReplacementData building)
    {
        building = new BuildingReplacementData();
        byte[] raw = Find(fileName, 2);
        if (raw == null)
            return false;
        using (BinaryReader r = new BinaryReader(new MemoryStream(raw)))
        {
            building.FactionId = r.ReadUInt16();
            building.BuildingType = r.ReadInt32();
            building.Quality = r.ReadByte();
            building.NameSeed = r.ReadUInt16();
            building.RmbSubRecord = SubRecord(r);
            int n = r.ReadInt32();
            building.AutoMapData = n >= 0 ? r.ReadBytes(n) : null;
            CheckEnd(r, fileName);
        }
        return true;
    }

    /// <summary>A location of the packs (location-R-L.json), as binary.</summary>
    public static bool TryGetLocation(string fileName, out DFLocation location)
    {
        location = new DFLocation();
        byte[] raw = Find(fileName, 3);
        if (raw == null)
            return false;
        using (BinaryReader r = new BinaryReader(new MemoryStream(raw)))
        {
            location.Loaded = r.ReadByte() != 0;
            location.Name = Str(r);
            location.RegionName = Str(r);
            location.HasDungeon = r.ReadByte() != 0;
            location.MapTableData.MapId = r.ReadInt32();
            location.MapTableData.Latitude = r.ReadInt32();
            location.MapTableData.Longitude = r.ReadInt32();
            location.MapTableData.LocationType = (DFRegion.LocationTypes)r.ReadInt32();
            location.MapTableData.DungeonType = (DFRegion.DungeonTypes)r.ReadInt32();
            location.MapTableData.Discovered = r.ReadByte() != 0;
            location.MapTableData.Key = r.ReadUInt32();
            location.Exterior.RecordElement.Header = RecordHeader(r);
            location.Exterior.BuildingCount = r.ReadUInt16();
            int n = r.ReadInt32();
            if (n >= 0)
            {
                location.Exterior.Buildings = new DFLocation.BuildingData[n];
                for (int i = 0; i < n; i++)
                    location.Exterior.Buildings[i] = Building(r);
            }
            location.Exterior.ExteriorData.AnotherName = Str(r);
            location.Exterior.ExteriorData.MapId = r.ReadInt32();
            location.Exterior.ExteriorData.LocationId = r.ReadUInt32();
            location.Exterior.ExteriorData.Width = r.ReadByte();
            location.Exterior.ExteriorData.Height = r.ReadByte();
            location.Exterior.ExteriorData.PortTownAndUnknown = r.ReadByte();
            n = r.ReadInt32();
            if (n >= 0)
            {
                location.Exterior.ExteriorData.BlockNames = new string[n];
                for (int i = 0; i < n; i++)
                    location.Exterior.ExteriorData.BlockNames[i] = Str(r);
            }
            location.Dungeon.RecordElement.Header = RecordHeader(r);
            location.Dungeon.Header.BlockCount = r.ReadUInt16();
            n = r.ReadInt32();
            if (n >= 0)
            {
                location.Dungeon.Blocks = new DFLocation.DungeonBlock[n];
                for (int i = 0; i < n; i++)
                {
                    location.Dungeon.Blocks[i].X = r.ReadSByte();
                    location.Dungeon.Blocks[i].Z = r.ReadSByte();
                    location.Dungeon.Blocks[i].IsStartingBlock = r.ReadByte() != 0;
                    location.Dungeon.Blocks[i].BlockName = Str(r);
                    location.Dungeon.Blocks[i].WaterLevel = r.ReadInt16();
                    location.Dungeon.Blocks[i].CastleBlock = r.ReadByte() != 0;
                }
            }
            location.Climate.WorldClimate = r.ReadInt32();
            location.Climate.ClimateType = (DFLocation.ClimateBaseType)r.ReadInt32();
            location.Climate.NatureSet = (DFLocation.ClimateTextureSet)r.ReadInt32();
            location.Climate.GroundArchive = r.ReadInt32();
            location.Climate.NatureArchive = r.ReadInt32();
            location.Climate.SkyBase = r.ReadInt32();
            location.Climate.People = (FactionFile.FactionRaces)r.ReadInt32();
            location.Climate.Names = (FactionFile.FactionRaces)r.ReadInt32();
            location.Politic = r.ReadInt32();
            location.RegionIndex = r.ReadInt32();
            location.LocationIndex = r.ReadInt32();
            CheckEnd(r, fileName);
        }
        return true;
    }

    static DFLocation.LocationRecordElementHeader RecordHeader(BinaryReader r)
    {
        DFLocation.LocationRecordElementHeader header = new DFLocation.LocationRecordElementHeader();
        header.X = r.ReadInt32();
        header.Y = r.ReadInt32();
        header.IsExterior = r.ReadUInt16();
        header.Unknown2 = r.ReadUInt32();
        header.LocationId = r.ReadUInt16();
        header.IsInterior = r.ReadUInt16();
        header.ExteriorLocationId = r.ReadUInt32();
        header.LocationName = Str(r);
        return header;
    }

    static DFLocation.BuildingData Building(BinaryReader r)
    {
        DFLocation.BuildingData building = new DFLocation.BuildingData();
        building.NameSeed = r.ReadUInt16();
        building.FactionId = r.ReadUInt16();
        building.Sector = r.ReadInt16();
        building.LocationId = r.ReadUInt16();
        building.BuildingType = (DFLocation.BuildingTypes)r.ReadInt32();
        building.Quality = r.ReadByte();
        return building;
    }

    // worldpack.py and these readers must agree field by field
    static void CheckEnd(BinaryReader r, string fileName)
    {
        if (r.BaseStream.Position != r.BaseStream.Length)
            Debug.LogWarningFormat("VitaWorldPack: {0} read {1} of {2} bytes (writer/reader mismatch)", fileName, r.BaseStream.Position, r.BaseStream.Length);
    }

    static byte[] Find(string fileName, int kind)
    {
        if (packs == null)
            Open();
        foreach (Pack pack in packs)
        {
            Entry entry;
            if (!pack.TryGetValue(fileName, out entry))
                continue;
            if (entry.kind != kind)
                return null;
            byte[] deflated = new byte[entry.deflated], raw = new byte[entry.raw];
            using (FileStream file = new FileStream(pack.path, FileMode.Open, FileAccess.Read))
            {
                file.Seek(entry.offset, SeekOrigin.Begin);
                ReadAll(file, deflated);
            }
            using (DeflateStream inflate = new DeflateStream(new MemoryStream(deflated), CompressionMode.Decompress))
                ReadAll(inflate, raw);
            return raw;
        }
        return null;
    }

    // The reader for vita/tools/worldpack.py's rmb_binary: same fields, same order
    static DFBlock ReadBlock(BinaryReader r)
    {
        DFBlock block = new DFBlock();
        block.Position = r.ReadInt64();
        block.Name = Str(r);
        block.Type = DFBlock.BlockTypes.Rmb;
        DFBlock.RmbBlockFld fld = new DFBlock.RmbBlockFld();
        fld.NumBlockDataRecords = r.ReadByte();
        fld.NumMisc3dObjectRecords = r.ReadByte();
        fld.NumMiscFlatObjectRecords = r.ReadByte();
        int n = r.ReadInt32();
        if (n >= 0)
        {
            fld.BlockPositions = new DFBlock.RmbFldBlockPositions[n];
            for (int i = 0; i < n; i++)
            {
                fld.BlockPositions[i].XPos = r.ReadInt32();
                fld.BlockPositions[i].ZPos = r.ReadInt32();
                fld.BlockPositions[i].YRotation = r.ReadInt32();
            }
        }
        n = r.ReadInt32();
        if (n >= 0)
        {
            fld.BuildingDataList = new DFLocation.BuildingData[n];
            for (int i = 0; i < n; i++)
                fld.BuildingDataList[i] = Building(r);
        }
        n = r.ReadInt32();
        if (n >= 0)
        {
            fld.BlockDataSizes = new int[n];
            for (int i = 0; i < n; i++)
                fld.BlockDataSizes[i] = r.ReadInt32();
        }
        if (r.ReadByte() != 0)
        {
            // RmbGroundDataConverter's order: y-major, stored at [x, y]
            n = r.ReadInt32();
            fld.GroundData.Header = r.ReadBytes(n);
            fld.GroundData.GroundTiles = new DFBlock.RmbGroundTiles[16, 16];
            fld.GroundData.GroundScenery = new DFBlock.RmbGroundScenery[16, 16];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    fld.GroundData.GroundTiles[x, y].TileBitfield = r.ReadByte();
                    fld.GroundData.GroundTiles[x, y].TextureRecord = r.ReadInt32();
                    fld.GroundData.GroundTiles[x, y].IsRotated = r.ReadByte() != 0;
                    fld.GroundData.GroundTiles[x, y].IsFlipped = r.ReadByte() != 0;
                }
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    fld.GroundData.GroundScenery[x, y].TextureRecord = r.ReadInt32();
        }
        n = r.ReadInt32();
        fld.AutoMapData = n >= 0 ? r.ReadBytes(n) : null;
        fld.Name = Str(r);
        n = r.ReadInt32();
        if (n >= 0)
        {
            fld.OtherNames = new string[n];
            for (int i = 0; i < n; i++)
                fld.OtherNames[i] = Str(r);
        }
        block.RmbBlock.FldHeader = fld;
        n = r.ReadInt32();
        if (n >= 0)
        {
            block.RmbBlock.SubRecords = new DFBlock.RmbSubRecord[n];
            for (int i = 0; i < n; i++)
                block.RmbBlock.SubRecords[i] = SubRecord(r);
        }
        block.RmbBlock.Misc3dObjectRecords = Objects3d(r);
        block.RmbBlock.MiscFlatObjectRecords = Flats(r);
        return block;
    }

    static DFBlock.RmbSubRecord SubRecord(BinaryReader r)
    {
        DFBlock.RmbSubRecord record = new DFBlock.RmbSubRecord();
        record.XPos = r.ReadInt32();
        record.ZPos = r.ReadInt32();
        record.YRotation = r.ReadInt32();
        record.Exterior = BlockData(r);
        record.Interior = BlockData(r);
        return record;
    }

    static DFBlock.RmbBlockData BlockData(BinaryReader r)
    {
        DFBlock.RmbBlockData data = new DFBlock.RmbBlockData();
        data.Header.Num3dObjectRecords = r.ReadByte();
        data.Header.NumFlatObjectRecords = r.ReadByte();
        data.Header.NumSection3Records = r.ReadByte();
        data.Header.NumPeopleRecords = r.ReadByte();
        data.Header.NumDoorRecords = r.ReadByte();
        data.Block3dObjectRecords = Objects3d(r);
        data.BlockFlatObjectRecords = Flats(r);
        int n = r.ReadInt32();
        if (n >= 0)
        {
            data.BlockSection3Records = new DFBlock.RmbBlockSection3Record[n];
            for (int i = 0; i < n; i++)
            {
                data.BlockSection3Records[i].XPos = r.ReadInt32();
                data.BlockSection3Records[i].YPos = r.ReadInt32();
                data.BlockSection3Records[i].ZPos = r.ReadInt32();
            }
        }
        n = r.ReadInt32();
        if (n >= 0)
        {
            data.BlockPeopleRecords = new DFBlock.RmbBlockPeopleRecord[n];
            for (int i = 0; i < n; i++)
            {
                data.BlockPeopleRecords[i].Position = r.ReadInt64();
                data.BlockPeopleRecords[i].XPos = r.ReadInt32();
                data.BlockPeopleRecords[i].YPos = r.ReadInt32();
                data.BlockPeopleRecords[i].ZPos = r.ReadInt32();
                data.BlockPeopleRecords[i].TextureArchive = r.ReadInt32();
                data.BlockPeopleRecords[i].TextureRecord = r.ReadInt32();
                data.BlockPeopleRecords[i].FactionID = r.ReadInt16();
                data.BlockPeopleRecords[i].Flags = r.ReadByte();
            }
        }
        n = r.ReadInt32();
        if (n >= 0)
        {
            data.BlockDoorRecords = new DFBlock.RmbBlockDoorRecord[n];
            for (int i = 0; i < n; i++)
            {
                data.BlockDoorRecords[i].Position = r.ReadInt32();
                data.BlockDoorRecords[i].XPos = r.ReadInt32();
                data.BlockDoorRecords[i].YPos = r.ReadInt32();
                data.BlockDoorRecords[i].ZPos = r.ReadInt32();
                data.BlockDoorRecords[i].YRotation = r.ReadInt16();
                data.BlockDoorRecords[i].OpenRotation = r.ReadInt16();
                data.BlockDoorRecords[i].DoorModelIndex = r.ReadByte();
            }
        }
        return data;
    }

    static DFBlock.RmbBlock3dObjectRecord[] Objects3d(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0)
            return null;
        DFBlock.RmbBlock3dObjectRecord[] objects = new DFBlock.RmbBlock3dObjectRecord[n];
        for (int i = 0; i < n; i++)
        {
            objects[i].ModelId = Str(r);
            objects[i].ModelIdNum = r.ReadUInt32();
            objects[i].ObjectType = r.ReadByte();
            objects[i].XPos = r.ReadInt32();
            objects[i].YPos = r.ReadInt32();
            objects[i].ZPos = r.ReadInt32();
            objects[i].XScale = r.ReadSingle();
            objects[i].YScale = r.ReadSingle();
            objects[i].ZScale = r.ReadSingle();
            objects[i].XRotation = r.ReadInt16();
            objects[i].YRotation = r.ReadInt16();
            objects[i].ZRotation = r.ReadInt16();
        }
        return objects;
    }

    static DFBlock.RmbBlockFlatObjectRecord[] Flats(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0)
            return null;
        DFBlock.RmbBlockFlatObjectRecord[] flats = new DFBlock.RmbBlockFlatObjectRecord[n];
        for (int i = 0; i < n; i++)
        {
            flats[i].Position = r.ReadInt64();
            flats[i].XPos = r.ReadInt32();
            flats[i].YPos = r.ReadInt32();
            flats[i].ZPos = r.ReadInt32();
            flats[i].TextureArchive = r.ReadInt32();
            flats[i].TextureRecord = r.ReadInt32();
            flats[i].FactionID = r.ReadInt16();
            flats[i].Flags = r.ReadByte();
        }
        return flats;
    }

    static string Str(BinaryReader r)
    {
        int n = r.ReadInt32();
        return n < 0 ? null : Encoding.UTF8.GetString(r.ReadBytes(n));
    }

    static void ReadAll(Stream stream, byte[] buffer)
    {
        int done = 0, n;
        while (done < buffer.Length && (n = stream.Read(buffer, done, buffer.Length - done)) > 0)
            done += n;
        if (done != buffer.Length)
            throw new IOException("VitaWorldPack: short read");
    }

    /// <summary>Every RMB block of one pack, for the block baker (editor). Leaves the packs to reopen from the settings.</summary>
    public static List<DFBlock> AllBlocks(string name)
    {
        packs = new List<Pack>();
        Add(name);
        List<DFBlock> blocks = new List<DFBlock>();
        foreach (Pack pack in packs)
            foreach (KeyValuePair<string, Entry> entry in pack.All())
            {
                DFBlock block;
                if (entry.Value.kind == 1 && TryGetBlock(entry.Key, out block))
                    blocks.Add(block);
            }
        packs = null;
        return blocks;
    }

    /// <summary>Reopens the packs from the settings on the next lookup (block baker: one pass per mod combination).</summary>
    public static void Reopen()
    {
        packs = null;
    }

    static void Open()
    {
        packs = new List<Pack>();
        SettingsManager settings = DaggerfallUnity.Settings;
        // The PC load order, last-loaded first: World of Daggerfall > Villages > Cities > Windmills > Fixed Desert Architecture > Finding My
        // Religion. Villages and Cities replace the windmill mod's 7 farm blocks with their own farms (Villages 1.4.2 has a mill in 2 of them),
        // as on the PC; windmills.pak still adds the mill to farms the two don't replace. Packs from vita/tools/worldpack.py / windmills.py.
        // World of Daggerfall (LoadPriority 143 in the PC Mods.json, the last of these, so it wins): 11 vanilla town blocks + 1 location
        // (location-17-1246, which Villages has too)
        if (settings.VitaWorldOfDaggerfall)
            Add("wod");
        if (settings.VitaBeautifulVillages)
            Add("villages");
        if (settings.VitaBeautifulCities)
            Add("cities");
        if (VitaWindmills.Enabled)
            Add("windmills");
        if (settings.VitaFixedDesertArchitecture)
            Add("fda");
        if (settings.VitaFindingMyReligion)
            Add("religion");
        // Fixed Dungeon Exteriors (Cliffworms 1.4.4) loads before all of the above on the PC; it shares no file with them
        if (settings.VitaFixedDungeonExteriors)
            Add("fde");
    }

    static Pack ReadIndex(string name)
    {
        string path = Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaMods"), name + ".pak");
        Pack pack = new Pack { path = path };
        using (BinaryReader reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read)))
        {
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "VWPK" || reader.ReadInt32() != 2)
                throw new IOException("not a version 2 pack");
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string entryName = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
                pack.Set(entryName, new Entry { kind = reader.ReadInt32(), offset = reader.ReadInt32(), deflated = reader.ReadInt32(), raw = reader.ReadInt32() });
            }
        }
        return pack;
    }

    static void Add(string name)
    {
        try
        {
            Pack pack = ReadIndex(name);
            packs.Add(pack);
            Debug.LogFormat("VitaWorldPack: {0} {1} entries", name, pack.Count);
        }
        catch (System.Exception e)
        {
            Debug.LogWarningFormat("VitaWorldPack: {0} not loaded: {1}", name, e.Message);
        }
    }

    /// <summary>
    /// A raw entry (kind 4) of a mod pack that isn't world data, e.g. Basic Roads' path maps in basicroads.pak (vita/tools/basicroads_pack.py).
    /// Opens the pack just for this read. Null if the pack or entry is missing or damaged.
    /// </summary>
    public static byte[] ReadRaw(string packName, string entryName)
    {
        try
        {
            Pack pack = ReadIndex(packName);
            Entry entry;
            if (!pack.TryGetValue(entryName, out entry) || entry.kind != 4)
                return null;
            byte[] deflated = new byte[entry.deflated], raw = new byte[entry.raw];
            using (FileStream file = new FileStream(pack.path, FileMode.Open, FileAccess.Read))
            {
                file.Seek(entry.offset, SeekOrigin.Begin);
                ReadAll(file, deflated);
            }
            using (DeflateStream inflate = new DeflateStream(new MemoryStream(deflated), CompressionMode.Decompress))
                ReadAll(inflate, raw);
            return raw;
        }
        catch (System.Exception e)
        {
            Debug.LogWarningFormat("VitaWorldPack: {0}/{1} not read: {2}", packName, entryName, e.Message);
            return null;
        }
    }
}
#endif
