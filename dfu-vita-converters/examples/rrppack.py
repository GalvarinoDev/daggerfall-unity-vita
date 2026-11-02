#!/usr/bin/env python3
# RMB Resource Pack prefabs -> jobs for Assets/Editor/Vita/VitaRrpBaker.cs (the port's rrppack.sh runs it).
#   python3 -I examples/rrppack.py <rrp source> <needed list> <copy dir> <jobs file> <unity asset prefix>
# Takes the needed prefabs that are one GameObject with a MeshFilter on an .fbx/.obj and RuntimeMaterials texture keys (vanilla archives),
# copies their mesh files into <copy dir> (no .meta: the 2018 editor imports with defaults, which match these metas: globalScale 1,
# useFileScale 1) and writes one job line per model: id|asset path|root scale|slot:archive:record;...
# Root rotations are left out: MeshReplacement.ImportCustomGameobject replaces them with the block's, and only multiplies the scale.
# Prefab instances of a model (Rocks, Market) take the model file from m_SourcePrefab and the root scale from their modifications.
# Left out (listed): .blend meshes (need Blender), crops/FX (scripts), project materials.
# Prefabs with RMBRocksMaterials (ports, some rocks: textures per player climate from CustomRuntimeMaterials/<id>.json) get key
# (9007, slot) per material and their tables go to <crm out> ("VCRM" v1, read by VitaDetModels.ApplyClimateTable): per slot the prefab's own
# material as archive, record (from its name "<archive>_<record>-0"), then per table key (CRM_KEYS order) default archive, record,
# winter archive, record; -1 = the key's list is empty, -2 = the list is shorter than the slot (the PC drops that material).
import os, re, shutil, struct, sys, json

src, needed, copy, jobs, prefix = sys.argv[1:6]
crm_out = sys.argv[6] if len(sys.argv) > 6 else None
# World of Daggerfall: <wod source> <wod id list "id wod/Prefabs/..."> <wod jobs file>: same rules, its own script and jobs file
wod_src, wod_ids, wod_jobs = sys.argv[7:10] if len(sys.argv) > 9 else (None, None, None)
CRM_SCRIPTS = ('58db90dd19fab182aa37614a95fff3de', '426f76434556e931a830bf5c83c73b54')     # RMBRocksMaterials.cs, WODRocksMaterials.cs (same rules)
CRM_ARCHIVE = 9007
CRM_KEYS = ['ocean', 'desert', 'desert2', 'mountain', 'rainforest', 'swamp', 'subtropical', 'mountainWoods', 'woodlands',
            'hauntedWoodlands', 'mountainBalfiera', 'mountainHammerfell']
crm_json = {}
for r, _, fs in os.walk(os.path.join(src, 'CustomRuntimeMaterials')):
    for f in fs:
        if f.endswith('.json'):
            crm_json[f[:-5]] = os.path.join(r, f)
prefab_names = {}
if wod_ids:
    for line in open(wod_ids):
        mid, prefab = line.split()
        prefab_names[mid] = os.path.basename(prefab)[:-7]
    for r, _, fs in os.walk(os.path.join(wod_src, 'CustomRuntimeMaterials')):
        for f in fs:
            if f.endswith('.json'):
                for mid in [m for m, n in prefab_names.items() if n == f[:-5]]:
                    crm_json[mid] = os.path.join(r, f)
crm_slots = []      # per slot: list of ints (2 + 12 * 4)

def crm_entry(lst, i):
    if not lst:
        return [-1, -1]
    if i >= len(lst):
        return [-2, -2]
    return [lst[i]['archive'], lst[i]['record']]

def crm_keys(mid, t):
    """Climate-table keys for an RMBRocksMaterials prefab: one (CRM_ARCHIVE, slot) per renderer material."""
    table = json.load(open(crm_json[mid], encoding='utf-8-sig'))
    mats = re.search(r'm_Materials:\n((?:  - \{.*\}\n)+)', t).group(1)
    keys = []
    for i, guid in enumerate(re.findall(r'guid: (\w+)', mats)):
        m = re.match(r'^(\d+)_(\d+)-\d+$', os.path.basename(g2p.get(guid, ''))[:-4])
        row = [int(m.group(1)), int(m.group(2))] if m else crm_entry(table.get('woodlands', {}).get('defaultMaterials'), i)
        for k in CRM_KEYS:
            v = table.get(k, {})
            row += crm_entry(v.get('defaultMaterials'), i) + crm_entry(v.get('winterMaterials'), i)
        # WoD's 100 slots share a few tables; RRP's slot numbers stay as they are (the block bake holds them)
        if mid not in prefab_names or row not in crm_slots:
            crm_slots.append(row)
        keys.append((str(i), str(CRM_ARCHIVE), str(len(crm_slots) - 1 if mid not in prefab_names else crm_slots.index(row))))
    return keys
g2p = {}
for tree in filter(None, (src, wod_src)):
  for r, _, fs in os.walk(tree):
    for f in fs:
        if f.endswith('.meta'):
            m = re.search(r'^guid: (\w+)', open(os.path.join(r, f), errors='replace').read(), re.M)
            if m:
                g2p[m.group(1)] = os.path.join(r, f[:-5])
os.makedirs(copy, exist_ok=True)
def add_jobs(root, list_file, jobs_file):
    out = []
    for line in open(list_file):
        mid, prefab = line.rstrip('\n').split(' ', 1)
        prefab = os.path.join(root, prefab.split('/', 1)[1])
        t = open(prefab).read()
        meshes = re.findall(r'^MeshFilter:.*?m_Mesh: \{fileID: -?\d+, guid: (\w+)', t, re.S | re.M)
        keys = re.findall(r'- Index: (\d+)\s+Archive: (\d+)\s+Record: (\d+)', t)
        source = re.findall(r'm_SourcePrefab: \{fileID: \d+, guid: (\w+)', t)
        if len(source) == 1 and not meshes:
            # Prefab instance: the root scale must be overridden (the default would be the model file's own)
            mod = dict(re.findall(r'propertyPath: (m_LocalScale\.[xyz])\s+value: ([-\d.e]+)', t))
            need = ['m_LocalScale.' + a for a in 'xyz']
            if not all(k in mod for k in need):
                skipped.append(mid + ' instance without root scale')
                continue
            meshes = source
            scale = tuple(mod[k] for k in need)
        elif meshes:
            scale = re.search(r'm_LocalScale: \{x: ([-\d.e]+), y: ([-\d.e]+), z: ([-\d.e]+)\}', t).groups()
        if not keys and any(x in t for x in CRM_SCRIPTS) and mid in crm_json and crm_out:
            keys = crm_keys(mid, t)
        if len(re.findall(r'^GameObject:', t, re.M)) != 1 or len(meshes) != 1 or not keys:
            skipped.append(mid + ' structure/keys')
            continue
        mesh = g2p.get(meshes[0], '')
        if not mesh.lower().endswith(('.fbx', '.obj')):
            skipped.append(mid + ' ' + os.path.basename(mesh))
            continue
        name = os.path.basename(mesh)
        shutil.copy(mesh, os.path.join(copy, name))
        out.append('%s|%s/%s|%s|%s' % (mid, prefix, name, ','.join(scale), ';'.join(':'.join(k) for k in keys)))
    open(jobs_file, 'w').write('\n'.join(out) + '\n')
    return len(out)

skipped = []
n = add_jobs(src, needed, jobs)
if wod_src:
    n += add_jobs(wod_src, wod_ids, wod_jobs)
if crm_out:
    b = bytearray(b'VCRM' + struct.pack('<iii', 1, len(CRM_KEYS), len(crm_slots)))
    for row in crm_slots:
        b += struct.pack('<%di' % len(row), *row)
    open(crm_out, 'wb').write(b)
    print('rrppack: %d climate-table slots -> %s' % (len(crm_slots), crm_out))
print('rrppack: %d jobs, skipped %d: %s' % (n, len(skipped), ', '.join(skipped)))
