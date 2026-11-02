#!/usr/bin/env python3
# A mod's model texture archive (PNGs "<archive>_<record>-0.png") -> VitaNative/TEXTURE.<archive> (VTEX 1, Daggerfall's ART_PAL, nearest
# colour; alpha < 128 -> index 0), read by TextureFile like DET's archives (detpack.py writes the same format).
#   python3 generic/modtexpack.py <ARENA2 dir> <out dir> <png>...     (first use: RMB Resource Pack archive 1401, the ports' wood)
# Prints the palette fit per record (mean / max colour distance): 0 = the art is Daggerfall palette art.
import os, re, struct, sys
import numpy as np
from PIL import Image

arena2, out, pngs = sys.argv[1], sys.argv[2], sys.argv[3:]
raw = open(os.path.join(arena2, 'ART_PAL.COL'), 'rb').read()
pal = np.frombuffer(raw[8:776] if len(raw) == 776 else raw[:768], np.uint8).reshape(256, 3).astype(np.int32)
if pal.max() < 64:
    pal = np.minimum(pal * 4, 255)
records, archive = {}, None
for p in pngs:
    m = re.match(r'^(\d+)_(\d+)-0\.png$', os.path.basename(p))
    archive = int(m.group(1))
    rgba = np.array(Image.open(p).convert('RGBA'))
    rgb = rgba[..., :3].reshape(-1, 3).astype(np.int32)
    u, inv = np.unique(rgb, axis=0, return_inverse=True)
    inv = inv.reshape(-1)
    d = ((u[:, None, :] - pal[None, 1:, :]) ** 2).sum(2)
    idx = (d.argmin(1).astype(np.uint8) + 1)[inv]
    err = np.sqrt(d.min(1))[inv]
    idx[rgba[..., 3].reshape(-1) < 128] = 0
    records[int(m.group(2))] = idx.reshape(rgba.shape[:2])     # rows top-down, as detpack.py writes them
    print('%s: %dx%d, ART_PAL mean %.2f max %.1f' % (os.path.basename(p), rgba.shape[1], rgba.shape[0], err.mean(), err.max()))
blank = np.zeros((1, 1), np.uint8)
b = bytearray(b'VTEX' + struct.pack('<ii', 1, max(records) + 1))
for r in range(max(records) + 1):
    idx = records.get(r, blank)
    b += struct.pack('<ii', idx.shape[1], idx.shape[0]) + idx.tobytes()
open(os.path.join(out, 'TEXTURE.%d' % archive), 'wb').write(b)
print('TEXTURE.%d: %d records' % (archive, max(records) + 1))
