#!/usr/bin/env python3
# Daggerfall Expanded Textures models -> Vita-native data, read by Assets/Scripts/Vita/VitaDetModels.cs.
#   python3 -I generic/detpack.py <det .dfmod> <ARENA2 dir> <out dir> [<compare dir>]
#   python3 -I generic/detpack.py <mod .dfmod> <ARENA2 dir> <out dir> [<compare dir>] --named <name> <archive>
# --named (New Paintings: paintings 9003, DIEP: diep 9004): any other model mod. No climate tables; every material becomes a record of
# <archive> (TEXTURE.<archive>, <name>_models.bin), as on the PC, where FinaliseMaterials keeps the bundled textures even for
# "TEXTURE.xxx_yy" names: the material's texture times its _Color, or a 16x16 of _Color where it has none. Textures are box-scaled to
# at most 256 texels a side (the mods ship 512-1024 photos; VitaAtlas takes only up to 512). One prefab per id (DIEP has two copies).
# --only <id,id,...>: just those prefabs (RMB Resource Pack hills/dome, whose .blend sources rrppack.sh can't read: rrphills 9005).
# DFU on the PC instantiates each DET prefab as its own GameObject; on the Vita MeshReader.GetModelData returns these models as ModelData,
# so RMBLayout's combiner merges them into the block mesh like vanilla models (and the block baker bakes them).
# Writes into <out dir> (StreamingAssets/VitaNative):
#   det_models.bin  "VDTM" int32 1, int32 count, per model: uint32 id, 3 floats size (DF units, for StaticBuilding), int32 vertex count,
#                   positions, normals (3 floats), UVs (2 floats), int32 submesh count, per submesh: int32 archive, int32 record,
#                   int32 index count, int32 indices. Meshes as the prefab's MeshFilter has them (ImportCustomGameobject puts the prefab at
#                   the record's matrix and multiplies its scale, all 1 here).
#   TEXTURE.9000    DET's own archive 10000 (materials "10000_<record>-0", what DET's climate tables name), palette indices.
#   TEXTURE.9001    the same records with DET's winter swaps applied (VitaDetModels: ClimateSwaps picks 9001 in winter).
#   TEXTURE.9002    DET's other model textures, one record each (by material, in name order).
#   det_regions.bin "VDRG" int32 1, int32 region count, per region: int32 length + UTF-8 name, int32 slot count, per slot: int32 archive,
#                   int32 record (the prefab's own material), then per region: default archive, record, winter archive, record. DET's
#                   banners (45179-45182) carry DETCustomRegionMaterials: at Start it sets the renderer's materials from the table for
#                   PlayerGPS.CurrentRegionName (winter list when the season is Winter), with no climate swap. Their submeshes get key
#                   (9006, slot) and VitaDetModels.ApplyRegion resolves it the same way (10000 -> 9000; regions not in a table keep the
#                   prefab's material, as DET does).
#   TEXTURE.9xxx format "VTEX" int32 1, int32 count, per record: int32 width, int32 height, width*height palette indices, rows top-down
#   (as DFBitmap.Data). Colours: nearest ART_PAL colour, index 0 (transparent) left out except for alpha < 128.
# Submesh keys: a model with a DET climate table (TextAsset "<id>") uses its default materials (archive 10000 -> 9000); otherwise the
# material name "<archive>_<record>-<frame>" (10000 -> 9000), "TEXTURE.<archive>_<record>..." (vanilla), or else a 9002 record.
# Prefabs with children or CustomDoor components (45171/45172) are left out: they stay unsupported until doors are ported.
# With <compare dir>: original | palette sheets of every packed texture, worst mean error first (judge the look on the PC).
import json, os, re, struct, sys
import numpy as np
import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler
from PIL import Image, ImageDraw

args = sys.argv[1:]
named_mode = None
if '--named' in args:
    i = args.index('--named')
    named_mode = (args[i + 1], int(args[i + 2]))
    del args[i:i + 3]
only = None
if '--only' in args:
    i = args.index('--only')
    only = set(args[i + 1].split(','))
    del args[i:i + 2]
det, arena2, out = args[0:3]
compare = args[3] if len(args) > 3 else None
NAMED_MAX = 256
GLOBAL_SCALE = 0.025
DET_ARCHIVE, VITA_ARCHIVE, VITA_WINTER, VITA_NAMED, VITA_REGION = 10000, 9000, 9001, 9002, 9006

raw = open(os.path.join(arena2, 'ART_PAL.COL'), 'rb').read()
pal = np.frombuffer(raw[8:776] if len(raw) == 776 else raw[:768], np.uint8).reshape(256, 3).astype(np.int32)
if pal.max() < 64:
    pal = np.minimum(pal * 4, 255)

env = UnityPy.load(det)
objects = list(env.objects)
texts = {o.read().m_Name: o.read().m_Script for o in objects if o.type.name == 'TextAsset'}
materials = {}      # name -> texture object
tints = {}          # name -> _Color (--named)
opaque = set()      # Standard materials in opaque mode: texture alpha unused (--named)
for o in objects:
    if o.type.name == 'Material':
        m = o.read()
        tex = [v.m_Texture for k, v in m.m_SavedProperties.m_TexEnvs if k == '_MainTex' and v.m_Texture.path_id]
        if tex:
            materials[m.m_Name] = tex[0].read()
        c = [v for k, v in m.m_SavedProperties.m_Colors if k == '_Color']
        tints[m.m_Name] = (c[0].r, c[0].g, c[0].b) if c else (1.0, 1.0, 1.0)
        mode = [v for k, v in m.m_SavedProperties.m_Floats if k == '_Mode']
        if m.m_Shader.path_id and m.m_Shader.read().m_ParsedForm.m_Name == 'Standard' and not (mode and mode[0]):
            opaque.add(m.m_Name)

def table(model_id):
    if named_mode:
        return None
    script = texts.get(str(model_id))
    if script is None:
        return None
    try:
        t = json.loads(re.sub(r',(\s*[\]}])', r'\1', str(script)))
    except ValueError as e:
        bad_tables.append('%s (%s)' % (model_id, e))
        return None
    if not isinstance(t, dict) or not isinstance(t.get('woodlands'), dict):
        bad_tables.append('%s (not a climate table)' % model_id)
        return None
    return t

bad_tables = []

def region_table(model_id):
    # DETCustomRegionMaterials' table; FullSerializer reads 45181's "record": 09 as 9, so leading zeros are stripped first
    if named_mode or str(model_id) not in texts:
        return None
    script = str(texts[str(model_id)])
    if '"regionName"' not in script:
        return None
    return json.loads(re.sub(r'(:\s*)0+(\d)', r'\1\2', script))['regions']

region_slots = []   # (prefab key, {region name: (default key, winter key)})
missing_region_textures = set()

def named_rgba(name):
    tint = np.array(tints.get(name, (1.0, 1.0, 1.0)))
    if name not in materials:
        rgb = np.round(np.minimum(tint, 1.0) * 255).astype(np.uint8)
        return np.concatenate([np.tile(rgb, (16, 16, 1)), np.full((16, 16, 1), 255, np.uint8)], 2)
    img = materials[name].image.convert('RGBA')
    scale = min(1.0, NAMED_MAX / float(max(img.size)))
    if scale < 1:
        img = img.resize((max(1, int(img.size[0] * scale)), max(1, int(img.size[1] * scale))), Image.BOX)
    rgba = np.array(img)
    rgba[..., :3] = np.round(np.minimum(rgba[..., :3] * tint, 255)).astype(np.uint8)
    if name in opaque:
        rgba[..., 3] = 255
    return rgba

def to_palette(texture):
    rgba = named_rgba(texture) if named_mode else np.array(texture.image.convert('RGBA'))     # named: a material name
    rgb = rgba[..., :3].reshape(-1, 3).astype(np.int32)
    idx = ((rgb[:, None, :] - pal[None, 1:, :]) ** 2).sum(2).argmin(1).astype(np.uint8) + 1
    idx[rgba[..., 3].reshape(-1) < 128] = 0
    return rgba, idx.reshape(rgba.shape[:2])

# Winter swaps of archive 10000, from the climate tables (woodlands: a temperate climate that has snow)
winter = {}
named = {}          # material name -> 9002 record
models = []
skipped = []
for o in objects:
    if o.type.name != 'GameObject':
        continue
    go = o.read()
    if not go.m_Name.isdigit() or (only and go.m_Name not in only):
        continue
    if named_mode and any(m[0] == int(go.m_Name) for m in models):
        continue
    comps = [(c.component if hasattr(c, 'component') else c).read() for c in go.m_Components]
    kinds = [c.object_reader.type.name for c in comps]
    transform = comps[kinds.index('Transform')]
    if len(transform.m_Children) or 'MonoBehaviour' in kinds or 'MeshFilter' not in kinds:
        skipped.append(go.m_Name)
        continue
    mesh = comps[kinds.index('MeshFilter')].m_Mesh.read()
    renderer = comps[kinds.index('MeshRenderer')]
    mats = [m.read().m_Name if m.path_id else None for m in renderer.m_Materials]
    t = table(go.m_Name)
    rt = region_table(go.m_Name)
    keys = []
    for i in range(len(mesh.m_SubMeshes)):
        if t and i < len(t['woodlands']['defaultMaterials']):
            d, w = t['woodlands']['defaultMaterials'][i], t['woodlands']['winterMaterials'][i]
            assert d['archive'] == DET_ARCHIVE == w['archive'], go.m_Name
            assert winter.setdefault(d['record'], w['record']) == w['record'], (go.m_Name, d, w)
            keys.append((VITA_ARCHIVE, d['record']))
            continue
        name = mats[min(i, len(mats) - 1)]
        if named_mode:
            assert name in tints, (go.m_Name, name)
            keys.append((named_mode[1], named.setdefault(name, len(named))))
            continue
        m = re.match(r'^(\d+)_(\d+)-\d+$', name) or re.match(r'^TEXTURE\.(\d+)_(\d+)', name)
        if m and int(m.group(1)) == DET_ARCHIVE:
            keys.append((VITA_ARCHIVE, int(m.group(2))))
        elif m and int(m.group(1)) < 512:
            keys.append((int(m.group(1)), int(m.group(2))))
        else:
            assert name in materials, (go.m_Name, name)
            keys.append((VITA_NAMED, named.setdefault(name, len(named))))
    if rt:
        def vita_key(m):
            return (VITA_ARCHIVE if m['archive'] == DET_ARCHIVE else m['archive'], m['record'])
        for i in range(min(len(keys), len(rt[0]['defaultMaterials']))):
            region_slots.append((keys[i], {r['regionName']: (vita_key(r['defaultMaterials'][i]), vita_key(r['winterMaterials'][i])) for r in rt}))
            keys[i] = (VITA_REGION, len(region_slots) - 1)
    h = MeshHandler(mesh)
    h.process()
    models.append((int(go.m_Name), h, [[i for tri in sub for i in tri] for sub in h.get_triangles()], keys))
assert len(named) < 256

def write_textures(path, records):
    b = bytearray(b'VTEX' + struct.pack('<ii', 1, len(records)))
    for idx in records:
        b += struct.pack('<ii', idx.shape[1], idx.shape[0]) + idx.tobytes()
    open(path, 'wb').write(b)

os.makedirs(out, exist_ok=True)
if named_mode:
    converted = {n: to_palette(n) for n in named}
    write_textures(os.path.join(out, 'TEXTURE.%d' % named_mode[1]), [converted[n][1] for n in sorted(named, key=named.get)])
    VITA_NAMED, count = named_mode[1], 0
else:
    det_records = {int(re.match(r'^10000_(\d+)-0$', n).group(1)): n for n in materials if re.match(r'^10000_\d+-0$', n)}
    converted = {}
    for name in list(det_records.values()) + list(named):
        converted[name] = to_palette(materials[name])
    blank = np.zeros((1, 1), np.uint8)
    count = max(det_records) + 1
    summer = [converted[det_records[r]][1] if r in det_records else blank for r in range(count)]
    write_textures(os.path.join(out, 'TEXTURE.%d' % VITA_ARCHIVE), summer)
    write_textures(os.path.join(out, 'TEXTURE.%d' % VITA_WINTER), [summer[winter.get(r, r)] for r in range(count)])
    write_textures(os.path.join(out, 'TEXTURE.%d' % VITA_NAMED), [converted[n][1] for n in sorted(named, key=named.get)])
    regions = sorted({name for _, table_ in region_slots for name in table_})
    b = bytearray(b'VDRG' + struct.pack('<ii', 1, len(regions)))
    for name in regions:
        b += struct.pack('<i', len(name.encode('utf-8'))) + name.encode('utf-8')
    b += struct.pack('<i', len(region_slots))
    for prefab, table_ in region_slots:
        b += struct.pack('<ii', *prefab)
        for name in regions:
            d, w = table_.get(name, (prefab, prefab))
            for archive, record in (d, w):
                if archive == VITA_ARCHIVE and record not in det_records:
                    # DET's table names a texture its bundle lacks (Bhoriane: 10000_64): on the PC that material fails to load and the
                    # renderer gets one material fewer (garbled banner); here the slot keeps the prefab's own texture
                    missing_region_textures.add((name, record))
                    archive, record = prefab
                b += struct.pack('<ii', archive, record)
    open(os.path.join(out, 'det_regions.bin'), 'wb').write(b)
    print('detpack: %d region slots, %d regions; table textures missing from the bundle (prefab texture kept): %s' % (
        len(region_slots), len(regions), sorted(missing_region_textures)))

b = bytearray(b'VDTM' + struct.pack('<ii', 1, len(models)))
for model_id, h, subs, keys in sorted(models, key=lambda m: m[0]):
    v = np.array([p[:3] for p in h.m_Vertices], np.float32)
    size = (v.max(0) - v.min(0)) / GLOBAL_SCALE
    b += struct.pack('<I3fi', model_id, *size, len(v))
    b += v.tobytes()
    b += np.array([n[:3] for n in h.m_Normals], np.float32).tobytes()
    b += np.array([uv[:2] for uv in h.m_UV0], np.float32).tobytes()
    b += struct.pack('<i', len(subs))
    for sub, (archive, record) in zip(subs, keys):
        b += struct.pack('<iii', archive, record, len(sub)) + np.array(sub, np.int32).tobytes()
open(os.path.join(out, (named_mode[0] if named_mode else 'det') + '_models.bin'), 'wb').write(b)
print('detpack: %d models (%d left out: %s), archive %d %d records (%d winter swaps), archive %d %d records, %.1f KB models' % (
    len(models), len(skipped), ' '.join(skipped), VITA_ARCHIVE, count, len(winter), VITA_NAMED, len(named), len(b) / 1024.0))
if bad_tables:
    print('detpack: climate tables not used (material names instead): ' + '; '.join(bad_tables))

if compare:
    os.makedirs(compare, exist_ok=True)
    pairs = []
    for name, (rgba, idx) in converted.items():
        solid = rgba[..., 3] >= 128
        back = pal[idx].astype(np.uint8)
        err = np.abs(back.astype(np.int32) - rgba[..., :3])[solid]
        shown = rgba.copy()
        shown[..., :3] = np.where(solid[..., None], back, rgba[..., :3])
        pairs.append((float(err.mean()) if err.size else 0.0, name, Image.fromarray(rgba, 'RGBA'), Image.fromarray(shown, 'RGBA')))
    pairs.sort(key=lambda p: -p[0])
    cell, cols = 160, 4
    for k in range(0, len(pairs), 40):
        part = pairs[k:k + 40]
        img = Image.new('RGB', (cols * (2 * cell + 12), ((len(part) + cols - 1) // cols) * (cell + 16)), (40, 40, 40))
        draw = ImageDraw.Draw(img)
        for i, (e, name, a, p) in enumerate(part):
            x, y = (i % cols) * (2 * cell + 12), (i // cols) * (cell + 16)
            for j, src in enumerate((a, p)):
                s = src.copy()
                s.thumbnail((cell, cell), Image.NEAREST)
                img.paste(s, (x + j * cell, y + 14), s)
            draw.text((x + 2, y + 1), '%s  mean %.1f' % (name[:30], e), fill=(230, 230, 230))
        img.save(os.path.join(compare, 'sheet_%d.png' % (k // 40 + 1)))
    print('detpack: %d textures compared, mean error %.2f, worst %.2f -> %s' % (
        len(pairs), np.mean([p[0] for p in pairs]), pairs[0][0], compare))
