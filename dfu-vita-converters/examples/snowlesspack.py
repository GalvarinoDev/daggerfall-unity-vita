#!/usr/bin/env python3
# SphincterVision Snowless Swamp and Jungle (texture replacements of TEXTURE.403 swamp winter ground and the swamp winter building
# archives) -> VTEX archives that VitaTextureBaker bakes into StreamingAssets/VitaNative/snowless.pak.
#   python3 -I examples/snowlesspack.py <mod .dfmod> <ARENA2 dir> <out dir> [<compare png>]
# Writes <out dir>/TEXTURE.<archive> in detpack.py's "VTEX" version 1 layout (int32 count, per record int32 width, height, palette
# indices top-down), one per archive the mod touches, every record of the vanilla file. Colours: nearest ART_PAL colour 1-254 (255 is
# DFU's window index, 0 transparent for alpha < 128); where the mod's <name>_Emission is bright the pixel becomes 255, so the Vita's
# palette path lights the same windows. Records the mod lacks are 1x1 index 0 (only TEXTURE.436, a 5-record stub that is one of DFU's
# UnsupportedFilenames: the PC never shows it and the bake skips it).
# <compare png>: original | palettized for every texture, worst mean error first.
import os, re, struct, sys
import numpy as np
import UnityPy
from PIL import Image

mod, arena2, out = sys.argv[1:4]
compare = sys.argv[4] if len(sys.argv) > 4 else None

raw = open(os.path.join(arena2, 'ART_PAL.COL'), 'rb').read()
pal = np.frombuffer(raw[8:776] if len(raw) == 776 else raw[:768], np.uint8).reshape(256, 3).astype(np.int32)
if pal.max() < 64:
    pal = np.minimum(pal * 4, 255)

textures, emission = {}, {}
for o in UnityPy.load(mod).objects:
    if o.type.name != 'Texture2D':
        continue
    t = o.read()
    m = re.fullmatch(r'(\d+)_(\d+)-0(_Emission)?', t.m_Name)
    if not m:
        print('snowless: skipped', t.m_Name)
        continue
    (emission if m.group(3) else textures)[(int(m.group(1)), int(m.group(2)))] = np.array(t.image.convert('RGBA'))

def vanilla_count(archive):
    return struct.unpack_from('<h', open(os.path.join(arena2, 'TEXTURE.%03d' % archive), 'rb').read(), 0)[0]

os.makedirs(out, exist_ok=True)
sheet = []
for archive in sorted({a for a, r in textures}):
    count = vanilla_count(archive)
    blob = [b'VTEX', struct.pack('<ii', 1, count)]
    for record in range(count):
        rgba = textures.get((archive, record))
        if rgba is None:
            print('snowless: TEXTURE.%03d record %d not in the mod: 1x1 transparent' % (archive, record))
            blob.append(struct.pack('<ii', 1, 1) + b'\0')
            continue
        rgb = rgba[..., :3].reshape(-1, 3).astype(np.int32)
        idx = ((rgb[:, None, :] - pal[None, 1:255, :]) ** 2).sum(2).argmin(1).astype(np.uint8) + 1
        idx[rgba[..., 3].reshape(-1) < 128] = 0
        glow = emission.get((archive, record))
        if glow is not None:
            idx[glow[..., :3].reshape(-1, 3).max(1) >= 128] = 255
        h, w = rgba.shape[:2]
        blob.append(struct.pack('<ii', w, h) + idx.tobytes())
        err = np.abs(pal[idx].reshape(h, w, 3) - rgba[..., :3].astype(np.int32))[rgba[..., 3] >= 128].mean()
        sheet.append((err, archive, record, rgba, pal[idx].reshape(h, w, 3).astype(np.uint8)))
    for a, r in textures:
        if a == archive and r >= count:
            print('snowless: TEXTURE.%03d_%d beyond the vanilla %d records: unused, as on the PC' % (a, r, count))
    open(os.path.join(out, 'TEXTURE.%03d' % archive), 'wb').write(b''.join(blob))
print('snowless: %d archives, %d textures (%d with emission) -> %s' % (len({a for a, r in textures}), len(sheet), len(emission), out))

if compare:
    sheet.sort(key=lambda s: -s[0])
    cols = 8
    img = Image.new('RGB', (cols * 136, ((len(sheet) + cols - 1) // cols) * 132), (40, 40, 40))
    for i, (err, a, r, src, dst) in enumerate(sheet):
        x, y = (i % cols) * 136, (i // cols) * 132
        img.paste(Image.fromarray(src[..., :3]).resize((64, 128 if src.shape[0] > 64 else 64)), (x, y))
        img.paste(Image.fromarray(dst).resize((64, 128 if src.shape[0] > 64 else 64)), (x + 66, y))
    img.save(compare)
    print('snowless: worst mean error %.1f (%d_%d), median %.1f -> %s' % (sheet[0][0], sheet[0][1], sheet[0][2], sheet[len(sheet) // 2][0], compare))
