#!/usr/bin/env python3
# Mod flat sprites -> Vita-native palette TEXTURE files, read by TextureFile.VitaLoad, so they take
# the vanilla flat path (palette atlas baked into atlas.pak by VitaAtlasBaker, billboard batches) instead of one RGBA texture + draw each.
#   python3 -I generic/flatpack.py <ARENA2 dir> <out dir> <report dir> <archive> [<archive> ...] -- <png dir> [<png dir> ...]
#       -- <world data json dir> [...]
# Only the records the world data places are written (DET's 10010: 51 of 142, a 1024² atlas instead of 2048²); the others become 1x1
# transparent stubs so record numbers stay.
# <png dir>: "<archive>_<record>-<frame>.png" + optional "<archive>_<record>-0.xml" / "<archive>_<record>.xml" (scaleX/scaleY) as the
# mods ship them (DET flats exported from its bundle, RMB Resource Pack sprites in Resources/VitaModTextures).
# Same size as on the PC: DFU sizes a non-classic flat as its PNG (record scale 1) times the XML scale (MaterialReader.GetMaterial,
# TextureReplacement.GetStaticBillboardMaterial); here the XML scale becomes the record's Daggerfall scale (MeshReader.GetBillboardMesh:
# size + (int)(size * scale / 256)), nearest whole texel.
# Colours: Daggerfall's palette (ART_PAL.COL, nearest colour) or the archive's own 255-colour palette, whichever is closer; index 0 is
# transparent (alpha < 128, as the cutout shader). Archives whose error passes the threshold are NOT written (they keep their original
# PNGs); <report dir>/report.txt lists every archive, sheet_<archive>.png shows original | converted for the flagged ones.
# Format "VTEX" int32 2, int32 record count, int32 palette flag (1 = 768 bytes RGB follow), then per record: int16 offsetX, offsetY,
# scaleX, scaleY, int32 width, height, frame count, frames (width * height indices each, rows top-down as DFBitmap.Data).
import glob, os, re, struct, sys
import numpy as np
from PIL import Image

ALL_RECORDS = {10030}         # nature sets: placed by the layout code, not by world data (World of Daggerfall - Biomes)
MAX_MEAN, MAX_P99 = 3.0, 24.0   # per-pixel mean |RGB| error over opaque pixels; 99th percentile (DET textures shipped at 2.2 / worst 6.1)

sep = [i for i, a in enumerate(sys.argv) if a == '--']
first, dirs, jsons = sys.argv[1:sep[0]], sys.argv[sep[0] + 1:sep[1]], sys.argv[sep[1] + 1:]
arena2, out, report_dir, archives = first[0], first[1], first[2], [int(a) for a in first[3:]]
raw = open(os.path.join(arena2, 'ART_PAL.COL'), 'rb').read()
art = np.frombuffer(raw[8:776] if len(raw) == 776 else raw[:768], np.uint8).reshape(256, 3).astype(np.int32)

used = set()
for d in jsons:
    for f in glob.glob(os.path.join(d, '**', '*.json'), recursive=True):
        for m in re.finditer(rb'"TextureArchive"\s*:\s*(\d+)\s*,\s*"TextureRecord"\s*:\s*(\d+)', open(f, 'rb').read()):
            used.add((int(m.group(1)), int(m.group(2))))

files = {}
for d in dirs:
    for p in glob.glob(os.path.join(d, '**', '*'), recursive=True):
        files.setdefault(os.path.basename(p).lower(), p)

def xml_scale(a, r):
    for name in ('%d_%d-0.xml' % (a, r), '%d_%d.xml' % (a, r)):
        if name in files:
            text = open(files[name]).read()
            get = lambda tag, d: float(re.search(r'<%s>\s*([-0-9.]+)' % tag, text).group(1)) if re.search(r'<%s>' % tag, text) else d
            return get('scaleX', 1.0), get('scaleY', 1.0)
    return 1.0, 1.0

def df_scale(size, xml):
    # PC: (size + int(size / 256)) * xml; Vita: size + int(size * s / 256), s an int16
    target = (size + int(size / 256)) * xml
    return min(range(-255, 4096), key=lambda s: abs(size + int(size * s / 256) - target))

def nearest(pixels, pal):
    # opaque pixels -> index 1..255 of pal (0 stays transparent)
    d = ((pixels[:, None, :] - pal[None, 1:, :]) ** 2).sum(2)
    return d.argmin(1) + 1

def own_palette(opaque):
    colours = np.unique(opaque, axis=0)
    pal = np.zeros((256, 3), np.int32)
    if len(colours) <= 255:
        pal[1:1 + len(colours)] = colours
        return pal
    img = Image.fromarray(opaque.reshape(1, -1, 3).astype(np.uint8)).quantize(255, method=Image.MEDIANCUT)
    q = np.array(img.getpalette()[:765], np.int32).reshape(255, 3)
    pal[1:] = q
    return pal

os.makedirs(out, exist_ok=True)
os.makedirs(report_dir, exist_ok=True)
lines = []
for a in archives:
    records = {}
    for name in files:
        m = re.match(r'%d_(\d+)-(\d+)\.png$' % a, name)
        if m and ((a, int(m.group(1))) in used or a in ALL_RECORDS):
            records.setdefault(int(m.group(1)), {})[int(m.group(2))] = files[name]
    if not records:
        lines.append('%d: no PNGs' % a)
        continue
    frames = {}
    for r, fr in records.items():
        frames[r] = [np.array(Image.open(fr[f]).convert('RGBA')).astype(np.int32) for f in range(len(fr)) if f in fr]
    opaque = np.concatenate([im.reshape(-1, 4)[im.reshape(-1, 4)[:, 3] >= 128, :3] for fs in frames.values() for im in fs])
    best = None
    for kind, pal in (('ART_PAL', art), ('own', own_palette(opaque))):
        u, inv = np.unique(opaque, axis=0, return_inverse=True)
        err = np.abs(pal[nearest(u, pal)] - u).mean(1)[inv.ravel()]
        score = (err.mean(), np.percentile(err, 99))
        if best is None or score < best[2]:
            best = (kind, pal, score)
    kind, pal, (mean, p99) = best
    ok = mean <= MAX_MEAN and p99 <= MAX_P99
    lines.append('%d: %d records, %d frames, %s palette, mean %.2f, p99 %.1f, %s' % (a, len(records), sum(len(f) for f in frames.values()),
        kind, mean, p99, 'written' if ok else 'FLAGGED: left as PNG'))
    sheet = []
    body = b''
    for r in range(max(records) + 1):
        fs = frames.get(r, [np.zeros((1, 1, 4), np.int32)])
        h = max(im.shape[0] for im in fs)
        w = max(im.shape[1] for im in fs)
        sx, sy = xml_scale(a, r) if r in records else (1.0, 1.0)
        body += struct.pack('<hhhhiii', 0, 0, df_scale(w, sx), df_scale(h, sy), w, h, len(fs))
        for im in fs:
            idx = np.zeros((h, w), np.uint8)
            px = im.reshape(-1, 4)
            sub = np.zeros(len(px), np.uint8)
            opq = px[:, 3] >= 128
            if opq.any():
                u, inv = np.unique(px[opq, :3], axis=0, return_inverse=True)
                sub[opq] = nearest(u, pal)[inv.ravel()]
            idx[:im.shape[0], :im.shape[1]] = sub.reshape(im.shape[:2])   # frames smaller than the record: top-left, rest transparent
            body += idx.tobytes()
            if not ok:
                conv = np.zeros((im.shape[0], im.shape[1], 4), np.uint8)
                conv[..., :3] = pal[sub.reshape(im.shape[:2])]
                conv[..., 3] = np.where(sub.reshape(im.shape[:2]) > 0, 255, 0)
                sheet.append(np.concatenate([im.astype(np.uint8), conv], 1))
    path = os.path.join(out, 'TEXTURE.%d' % a)
    if ok:
        head = struct.pack('<4sii i', b'VTEX', 2, max(records) + 1, 1 if kind == 'own' else 0)
        open(path, 'wb').write(head + (pal.astype(np.uint8).tobytes() if kind == 'own' else b'') + body)
    else:
        if os.path.exists(path):
            os.remove(path)
        W = max(s.shape[1] for s in sheet)
        canvas = Image.new('RGBA', (W, sum(s.shape[0] + 4 for s in sheet)), (255, 0, 255, 255))
        y = 0
        for s in sheet:
            canvas.paste(Image.fromarray(s), (0, y))
            y += s.shape[0] + 4
        canvas.save(os.path.join(report_dir, 'sheet_%d.png' % a))
open(os.path.join(report_dir, 'report.txt'), 'w').write('\n'.join(lines) + '\n')
print('\n'.join(lines))
