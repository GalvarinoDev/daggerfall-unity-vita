#!/usr/bin/env python3
# World-data mods (Beautiful Villages / Beautiful Cities, Android .dfmod) -> one StreamingAssets pack for VitaWorldPack.cs.
#   python3 -I generic/worldpack.py <out.pak> <ARENA2 dir> <json dir> [<extra model ids/flat archives file>]
# <json dir> holds the mod's TextAssets as files (dfmod_text.py <mod.dfmod> <dir>).
# Why a pack: loose WorldData JSON on the Vita means one File.Exists per lookup in a folder of thousands (~90 ms each) and
# 0.5-2.3 MB of pretty-printed JSON per block. The pack is one file: an index, then each entry as minified JSON, raw deflate.
# Objects whose model (ModelIdNum) or flat archive (TextureArchive) the Vita doesn't have (other PC mods: RMB Resource Pack,
# Daggerfall Expanded Textures, ...) are left out and counted; the record counts in each Header are kept in step.
# Extra file: lines "model <id>" / "flat <archive>" that the port supplies (windmills, ported packs).
# RMB blocks are stored ready to read: parsing one block's
# JSON with FullSerializer took 1.5-4 s on the Vita (Beautiful Cities). The binary holds DFBlock's public fields in a fixed order
# (VitaWorldPack.ReadBlock is the reader; FullSerializer's defaults for missing fields: 0, null arrays as length -1).
# Format: "VWPK" int32 version 2, int32 count, then per entry: int32 name length, UTF-8 name (the DFU file name, e.g.
# "FARMAA00.RMB.json"), int32 kind (0 JSON, 1 RMB block binary, 2 building binary, 3 location binary), int32 offset, int32 deflated length, int32 data length; then the data.
import collections, glob, json, os, re, struct, sys, zlib

def bsa_ids(path):
    d = open(path, "rb").read()
    count = struct.unpack_from("<h", d, 0)[0]
    return {struct.unpack_from("<I", d, len(d) - 8 * count + 8 * i)[0] for i in range(count)}

def load(path):
    text = open(path, "rb").read().decode("utf-8-sig")
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        # WALLAA11.RMB (Beautiful Cities 0.4.1): a lone backslash in a string; FullSerializer reads it as a plain character
        return json.loads(re.sub(r'\\(?!["\\/bfnrtu])', r'\\\\', text))

# Locations are read while the world streams in (each new map pixel with a town), so they are binary too.
# Enum values come from DFU's own source (FullSerializer writes enums by name).
def cs_enum(path, name):
    src = open(os.path.join(os.path.dirname(__file__), "..", "..", path), encoding="utf-8-sig").read()
    body = re.search(r"enum\s+%s\b[^{]*\{(.*?)\}" % name, src, re.S).group(1)
    values, v = {}, -1
    for item in re.sub(r"//[^\n]*", "", body).split(","):
        item = item.strip()
        if not item: continue
        k, _, e = item.partition("=")
        v = int(e.strip(), 0) if e.strip() else v + 1
        values[k.strip()] = v
    return values

ENUMS = {k: cs_enum(*v) for k, v in {
    "LocationType": ("Assets/Scripts/API/DFRegion.cs", "LocationTypes"), "DungeonType": ("Assets/Scripts/API/DFRegion.cs", "DungeonTypes"),
    "ClimateType": ("Assets/Scripts/API/DFLocation.cs", "ClimateBaseType"), "NatureSet": ("Assets/Scripts/API/DFLocation.cs", "ClimateTextureSet"),
    "People": ("Assets/Scripts/API/FactionFile.cs", "FactionRaces"), "Names": ("Assets/Scripts/API/FactionFile.cs", "FactionRaces")}.items()}

def enum(key, v):
    return ENUMS[key][v] if isinstance(v, str) else v

def location_binary(loc):
    w = W()
    w.i8(loc.get("Loaded")); w.str(loc.get("Name")); w.str(loc.get("RegionName")); w.i8(loc.get("HasDungeon"))
    m = loc.get("MapTableData") or {}
    w.i32(m.get("MapId")); w.i32(m.get("Latitude")); w.i32(m.get("Longitude")); w.i32(enum("LocationType", m.get("LocationType")))
    w.i32(enum("DungeonType", m.get("DungeonType"))); w.i8(m.get("Discovered")); w.u32(m.get("Key"))
    def element(e):
        h = (e or {}).get("Header") or {}
        w.i32(h.get("X")); w.i32(h.get("Y")); w.u16(h.get("IsExterior")); w.u32(h.get("Unknown2")); w.u16(h.get("LocationId"))
        w.u16(h.get("IsInterior")); w.u32(h.get("ExteriorLocationId")); w.str(h.get("LocationName"))
    x = loc.get("Exterior") or {}
    element(x.get("RecordElement")); w.u16(x.get("BuildingCount"))
    w.arr(x.get("Buildings"), lambda d: (w.u16(d.get("NameSeed")), w.u16(d.get("FactionId")), w.i16(d.get("Sector")),
        w.u16(d.get("LocationId")), w.i32(building_type(d.get("BuildingType"))), w.i8(d.get("Quality"))))
    e = x.get("ExteriorData") or {}
    w.str(e.get("AnotherName")); w.i32(e.get("MapId")); w.u32(e.get("LocationId")); w.i8(e.get("Width")); w.i8(e.get("Height"))
    w.i8(e.get("PortTownAndUnknown")); w.arr(e.get("BlockNames"), w.str)
    d = loc.get("Dungeon") or {}
    element(d.get("RecordElement")); w.u16((d.get("Header") or {}).get("BlockCount"))
    w.arr(d.get("Blocks"), lambda b: (w.i8(b.get("X")), w.i8(b.get("Z")), w.i8(b.get("IsStartingBlock")), w.str(b.get("BlockName")),
        w.i16(b.get("WaterLevel")), w.i8(b.get("CastleBlock"))))
    c = loc.get("Climate") or {}
    w.i32(c.get("WorldClimate")); w.i32(enum("ClimateType", c.get("ClimateType"))); w.i32(enum("NatureSet", c.get("NatureSet")))
    w.i32(c.get("GroundArchive")); w.i32(c.get("NatureArchive")); w.i32(c.get("SkyBase"))
    w.i32(enum("People", c.get("People"))); w.i32(enum("Names", c.get("Names")))
    w.i32(loc.get("Politic")); w.i32(loc.get("RegionIndex")); w.i32(loc.get("LocationIndex"))
    return bytes(w.b)

# DFLocation.BuildingTypes: FullSerializer writes enums by name
BUILDING_TYPES = {n: i for i, n in enumerate("Alchemist HouseForSale Armorer Bank Town4 Bookseller ClothingStore FurnitureStore GemStore "
    "GeneralStore Library GuildHall PawnShop WeaponSmith Temple Tavern Palace House1 House2 House3 House4 House5 House6 Town23 Ship".split())}
BUILDING_TYPES.update({"None": -1, "Special1": 0x74, "Special2": 0xdf, "Special3": 0xf9, "Special4": 0xfa, "AnyShop": 0xfffd,
    "AnyHouse": 0xfffe, "AllValid": 0xffff})

def building_type(v):
    return BUILDING_TYPES[v] if isinstance(v, str) else v

class W:
    def __init__(self): self.b = bytearray()
    def i8(self, v): self.b += struct.pack("<B", int(v or 0) & 0xFF)
    def i16(self, v): self.b += struct.pack("<h", int(v or 0))
    def u16(self, v): self.b += struct.pack("<H", int(v or 0))
    def i32(self, v): self.b += struct.pack("<i", int(v or 0))
    def u32(self, v): self.b += struct.pack("<I", int(v or 0))
    def i64(self, v): self.b += struct.pack("<q", int(v or 0))
    def f32(self, v): self.b += struct.pack("<f", float(v or 0))
    def str(self, v):
        if v is None: self.i32(-1); return
        e = v.encode("utf-8"); self.i32(len(e)); self.b += e
    def arr(self, v, item):
        if v is None: self.i32(-1); return
        self.i32(len(v))
        for x in v: item(x)

def rmb_header(w, blk):
    w.i64(blk.get("Position")); w.str(blk.get("Name"))
    fld = blk["RmbBlock"]["FldHeader"]
    w.i8(fld.get("NumBlockDataRecords")); w.i8(fld.get("NumMisc3dObjectRecords")); w.i8(fld.get("NumMiscFlatObjectRecords"))
    w.arr(fld.get("BlockPositions"), lambda p: (w.i32(p.get("XPos")), w.i32(p.get("ZPos")), w.i32(p.get("YRotation"))))
    w.arr(fld.get("BuildingDataList"), lambda d: (w.u16(d.get("NameSeed")), w.u16(d.get("FactionId")), w.i16(d.get("Sector")),
        w.u16(d.get("LocationId")), w.i32(building_type(d.get("BuildingType"))), w.i8(d.get("Quality"))))
    w.arr(fld.get("BlockDataSizes"), w.i32)
    g = fld.get("GroundData")
    if g and len(g) == 3:   # RmbGroundDataConverter: Header + 256 tiles + 256 scenery, y-major
        w.i8(1); w.arr(g["Header"], w.i8)
        for t in g["GroundTiles"]: w.i8(t.get("TileBitfield")); w.i32(t.get("TextureRecord")); w.i8(t.get("IsRotated")); w.i8(t.get("IsFlipped"))
        for t in g["GroundScenery"]: w.i32(t.get("TextureRecord"))
    else:
        w.i8(0)
    w.arr(fld.get("AutoMapData"), w.i8)
    w.str(fld.get("Name")); w.arr(fld.get("OtherNames"), w.str)

def rmb_binary(blk):
    w = W()
    if "RmbBlock" in blk:
        rmb_header(w, blk)
    rmb = blk.get("RmbBlock")
    def obj3d(o):
        w.str(o.get("ModelId")); w.u32(o.get("ModelIdNum")); w.i8(o.get("ObjectType"))
        w.i32(o.get("XPos")); w.i32(o.get("YPos")); w.i32(o.get("ZPos"))
        w.f32(o.get("XScale")); w.f32(o.get("YScale")); w.f32(o.get("ZScale"))
        w.i16(o.get("XRotation")); w.i16(o.get("YRotation")); w.i16(o.get("ZRotation"))
    def flat(o):   # flat and people records have the same public fields
        w.i64(o.get("Position")); w.i32(o.get("XPos")); w.i32(o.get("YPos")); w.i32(o.get("ZPos"))
        w.i32(o.get("TextureArchive")); w.i32(o.get("TextureRecord")); w.i16(o.get("FactionID")); w.i8(o.get("Flags"))
    def data(d):
        h = d.get("Header") or {}
        for k in ("Num3dObjectRecords", "NumFlatObjectRecords", "NumSection3Records", "NumPeopleRecords", "NumDoorRecords"): w.i8(h.get(k))
        w.arr(d.get("Block3dObjectRecords"), obj3d)
        w.arr(d.get("BlockFlatObjectRecords"), flat)
        w.arr(d.get("BlockSection3Records"), lambda o: (w.i32(o.get("XPos")), w.i32(o.get("YPos")), w.i32(o.get("ZPos"))))
        w.arr(d.get("BlockPeopleRecords"), flat)
        w.arr(d.get("BlockDoorRecords"), lambda o: (w.i32(o.get("Position")), w.i32(o.get("XPos")), w.i32(o.get("YPos")), w.i32(o.get("ZPos")),
            w.i16(o.get("YRotation")), w.i16(o.get("OpenRotation")), w.i8(o.get("DoorModelIndex"))))
    def subrecord(sr):
        w.i32(sr.get("XPos")); w.i32(sr.get("ZPos")); w.i32(sr.get("YRotation"))
        data(sr.get("Exterior") or {}); data(sr.get("Interior") or {})
    if "RmbBlock" not in blk:
        # A BuildingReplacementData (BLOCK.RMB-N-buildingM.json): 1.5 MB of them per market block took 11 s to parse
        w.u16(blk.get("FactionId")); w.i32(building_type(blk.get("BuildingType"))); w.i8(blk.get("Quality")); w.u16(blk.get("NameSeed"))
        subrecord(blk.get("RmbSubRecord") or {})
        w.arr(blk.get("AutoMapData"), w.i8)
        return bytes(w.b)
    w.arr(rmb.get("SubRecords"), subrecord)
    w.arr(rmb.get("Misc3dObjectRecords"), obj3d)
    w.arr(rmb.get("MiscFlatObjectRecords"), flat)
    return bytes(w.b)

def main():
    out, arena2, src = sys.argv[1:4]
    models = bsa_ids(os.path.join(arena2, "ARCH3D.BSA"))
    flats = {int(n.split(".")[1]) for n in os.listdir(arena2) if n.upper().startswith("TEXTURE.") and n.split(".")[1].isdigit()}
    if len(sys.argv) > 4:
        for line in open(sys.argv[4]):
            kind, _, value = line.partition(" ")
            if kind == "model": models.add(int(value))
            elif kind == "flat": flats.add(int(value))
    dropped = collections.Counter()

    def strip(x):
        if isinstance(x, dict):
            for k, v in list(x.items()):
                if isinstance(v, list) and v and isinstance(v[0], dict):
                    if "ModelIdNum" in v[0]:
                        keep = [o for o in v if o["ModelIdNum"] in models]
                        for o in v:
                            if o["ModelIdNum"] not in models: dropped["model %d" % o["ModelIdNum"]] += 1
                        x[k] = v = keep
                    elif "TextureArchive" in v[0]:
                        keep = [o for o in v if o["TextureArchive"] in flats]
                        for o in v:
                            if o["TextureArchive"] not in flats: dropped["flat %d" % o["TextureArchive"]] += 1
                        x[k] = v = keep
                strip(v)
            header = x.get("Header")
            if isinstance(header, dict) and "Num3dObjectRecords" in header:
                # Byte fields: the mods' interiors hold up to 1036 objects, and a count over 255 made FullSerializer throw on every
                # world init (endless city load). DFU only tests them for 0.
                header["Num3dObjectRecords"] = min(len(x.get("Block3dObjectRecords") or []), 255)
                header["NumFlatObjectRecords"] = min(len(x.get("BlockFlatObjectRecords") or []), 255)
        elif isinstance(x, list):
            for v in x: strip(v)

    entries = []
    for path in sorted(glob.glob(os.path.join(src, "*.json"))):
        name = os.path.basename(path)
        if not (name.startswith("location") or ".RMB" in name or ".RDB" in name):
            continue    # manifests, editor files
        data = load(path)
        strip(data)
        if name.endswith(".RMB.json") and data.get("Type") == "Rmb":
            kind, raw = 1, rmb_binary(data)
        elif "-building" in name and "RmbSubRecord" in data:
            kind, raw = 2, rmb_binary(data)
        elif name.startswith("location-") and "MapTableData" in data:
            kind, raw = 3, location_binary(data)
        else:
            kind, raw = 0, json.dumps(data, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        c = zlib.compressobj(9, zlib.DEFLATED, -15)
        entries.append((name, kind, c.compress(raw) + c.flush(), len(raw)))

    head = bytearray(b"VWPK" + struct.pack("<ii", 2, len(entries)))
    names = [e[0].encode("utf-8") for e in entries]
    offset = len(head) + sum(4 + len(n) + 16 for n in names)
    for n, (name, kind, blob, size) in zip(names, entries):
        head += struct.pack("<i", len(n)) + n + struct.pack("<iiii", kind, offset, len(blob), size)
        offset += len(blob)
    with open(out, "wb") as f:
        f.write(head)
        for e in entries: f.write(e[2])
    print("%s: %d entries (%d binary blocks, %d buildings, %d locations), %.1f MB (data %.0f MB)" % (out, len(entries), sum(e[1] == 1 for e in entries),
        sum(e[1] == 2 for e in entries), sum(e[1] == 3 for e in entries),
        offset / 1e6, sum(e[3] for e in entries) / 1e6))
    print("left out (not on the Vita):", sum(dropped.values()), dropped.most_common(25))

main()
