#!/usr/bin/env python3
# World of Daggerfall's material archives (Materials/**/<archive>_<record>-0.mat, what its CustomRuntimeMaterials name: mountains, rocks)
# -> VitaNative/TEXTURE.2000-2005 through generic/modtexpack.py (VTEX 1, ART_PAL). The mod's textures are 4096² flat-colour art (30-87 colours)
# and 64² rock tiles: box-scaled to at most 512 a side, what VitaAtlas takes.
#   python3 examples/wodtexpack.py <wod source> <ARENA2 dir> <out dir>
import glob, io, os, re, struct, subprocess, sys, tempfile
from PIL import Image

wod, arena2, out = sys.argv[1:4]
MAX = 512
g2p = {}
for r, _, fs in os.walk(wod):
    for f in fs:
        if f.endswith('.meta'):
            m = re.search(r'^guid: (\w+)', open(os.path.join(r, f), errors='replace').read(), re.M)
            if m:
                g2p[m.group(1)] = os.path.join(r, f[:-5])

def load(path):
    # PIL refuses some of these files (big text chunks): keep only the chunks that matter
    d = open(path, 'rb').read()
    o, i = bytearray(d[:8]), 8
    while i < len(d):
        n, t = struct.unpack('>I4s', d[i:i + 8])
        if t in (b'IHDR', b'PLTE', b'tRNS', b'IDAT', b'IEND'):
            o += d[i:i + 12 + n]
        i += 12 + n
    return Image.open(io.BytesIO(bytes(o))).convert('RGBA')

mats = {}
for m in glob.glob(os.path.join(wod, 'Materials', '**', '*.mat'), recursive=True):
    k = re.match(r'^(200\d)_(\d+)-0\.mat$', os.path.basename(m))
    if k:
        g = re.search(r'_MainTex:\s*\n\s*m_Texture: \{fileID: \d+, guid: (\w+)', open(m, errors='replace').read())
        mats[(int(k.group(1)), int(k.group(2)))] = g2p[g.group(1)]
cache = {}
with tempfile.TemporaryDirectory() as tmp:
    for archive in sorted({a for a, _ in mats}):
        files = []
        for (a, record), tex in sorted(mats.items()):
            if a == archive:
                if tex not in cache:
                    im = load(tex)
                    if max(im.size) > MAX:
                        im = im.resize((MAX, MAX * im.height // im.width), Image.BOX)
                    cache[tex] = im
                p = os.path.join(tmp, '%d_%d-0.png' % (a, record))
                cache[tex].save(p)
                files.append(p)
        subprocess.check_call([sys.executable, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'generic', 'modtexpack.py'), arena2, out] + files)
