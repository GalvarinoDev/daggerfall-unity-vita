#!/usr/bin/env python3
# World of Daggerfall - Biomes ground tile sets -> VTEX archives that VitaTextureBaker bakes into StreamingAssets/VitaNative/biomes.pak
# Same layout and colour rule as snowlesspack.py: nearest ART_PAL colour 1-254, 0 for alpha < 128.
#   python3 -I examples/biomespack.py <wod-biomes dir> <ARENA2 dir> <out dir> [<compare png> [<nature mask out>]]
import glob, io, os, re, struct, sys
import numpy as np
from PIL import Image

def load(path):
    # PIL refuses 58 of these files (big text chunks): keep only the chunks that matter, as wodtexpack.py
    d = open(path, 'rb').read()
    o, i = bytearray(d[:8]), 8
    while i < len(d):
        n, t = struct.unpack('>I4s', d[i:i + 8])
        if t in (b'IHDR', b'PLTE', b'tRNS', b'IDAT', b'IEND'):
            o += d[i:i + 12 + n]
        i += 12 + n
    return Image.open(io.BytesIO(bytes(o))).convert('RGBA')

src, arena2, out = sys.argv[1:4]
compare = sys.argv[4] if len(sys.argv) > 4 else None
mask_out = sys.argv[5] if len(sys.argv) > 5 else None
raw = open(os.path.join(arena2, 'ART_PAL.COL'), 'rb').read()
pal = np.frombuffer(raw[8:776] if len(raw) == 776 else raw[:768], np.uint8).reshape(256, 3).astype(np.int32)
if pal.max() < 64:
    pal = np.minimum(pal * 4, 255)

tiles = {}
for f in glob.glob(os.path.join(src, 'Textures', 'Terrain', '*', '*.png')):
    m = re.fullmatch(r'(\d+)_(\d+)-0\.png', os.path.basename(f))
    if m:
        tiles[(int(m.group(1)), int(m.group(2)))] = np.array(load(f))
    else:
        print('biomes: skipped', f)

os.makedirs(out, exist_ok=True)
sheet = []
for archive in sorted({a for a, r in tiles}):
    records = sorted(r for a, r in tiles if a == archive)
    assert records == list(range(56)), (archive, records)   # a ground archive is 56 tiles
    blob = [b'VTEX', struct.pack('<ii', 1, 56)]
    for record in records:
        rgba = tiles[(archive, record)]
        rgb = rgba[..., :3].reshape(-1, 3).astype(np.int32)
        idx = ((rgb[:, None, :] - pal[None, 1:255, :]) ** 2).sum(2).argmin(1).astype(np.uint8) + 1
        idx[rgba[..., 3].reshape(-1) < 128] = 0
        h, w = rgba.shape[:2]
        blob.append(struct.pack('<ii', w, h) + idx.tobytes())
        err = np.abs(pal[idx].reshape(h, w, 3) - rgba[..., :3].astype(np.int32))[rgba[..., 3] >= 128].mean()
        sheet.append((err, archive, record, rgba, pal[idx].reshape(h, w, 3).astype(np.uint8)))
    open(os.path.join(out, 'TEXTURE.%03d' % archive), 'wb').write(b''.join(blob))
sheet.sort(key=lambda s: -s[0])
print('biomes: %d archives, %d tiles, mean error worst %.1f (%d_%d), median %.1f' % (len({s[1] for s in sheet}), len(sheet), sheet[0][0], sheet[0][1], sheet[0][2], sheet[len(sheet) // 2][0]))
for archive in sorted({s[1] for s in sheet}):
    e = sorted(s[0] for s in sheet if s[1] == archive)
    print('biomes: TEXTURE.%03d median %.1f worst %.1f' % (archive, e[len(e) // 2], e[-1]))
if mask_out:
    # WODClimates.cs: nature archive 501 -> 10030 where climate_map.png is #FFA500. Row = map pixel Y (it flips Y, and Unity's y runs bottom-up).
    a = np.array(load(os.path.join(src, 'Assets', 'Maps', 'climate_map.png')))[..., :3]
    assert a.shape == (500, 1000, 3)
    bits = (a == (255, 165, 0)).all(2)
    open(mask_out, 'wb').write(np.packbits(bits, axis=1, bitorder='little').tobytes())
    print('biomes: %d orange map pixels -> %s' % (bits.sum(), mask_out))
if compare:
    cols = 12
    img = Image.new('RGB', (cols * 134, ((len(sheet) + cols - 1) // cols) * 68), (40, 40, 40))
    for i, (err, a, r, s, d) in enumerate(sheet):
        x, y = (i % cols) * 134, (i // cols) * 68
        img.paste(Image.fromarray(s[..., :3]), (x, y))
        img.paste(Image.fromarray(d), (x + 66, y))
    img.save(compare)
