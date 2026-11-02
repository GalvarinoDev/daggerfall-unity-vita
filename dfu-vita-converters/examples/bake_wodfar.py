#!/usr/bin/env python3
# Distant Terrain 3.69 full mode (World of Daggerfall) data for the port's far terrain (VitaFarTerrain).
#   python3 examples/bake_wodfar.py <distant terrain .dfmod> [<WOODS.WLD> <preview.png>]
# Writes Assets/StreamingAssets/VitaNative/wodfar.bin from the assets the PC mod ships (its 2048x1024 deriv map, its 3 CSVs),
# with DistantTerrain.cs's own maths (ApplyDerivativeHeightmap, ProcessMountainCsvToDefs, GetMountainModifiers):
#   int32 lifts, byte[62500] water mask (bit (y*1000+x) = map pixel x,y is deriv-map water; y = 0 is north),
#   lifts x (int32 y*1000+x, float32 lift as a fraction of MaxTerrainHeight, max over the cell's mountains).
# Every shipped mountain has radius 0.1*scale <= 0.1 < 0.7, i.e. DT's single-cell branch (the bake checks it).
# Winter-river opt-outs are all off in the mod's defaults, so the mask has no season.
import struct, sys
import numpy as np
import UnityPy
from PIL import Image

W, H = 1000, 500
MODS = {"WOD_Mountain_Small_01": .20, "WOD_Mountain_Small_02": .20, "WOD_Mountain_Small_03": .12, "WOD_Mountain_Small_04": .14,
        "WOD_Mountain_Small_05": .10, "WOD_Mountain_Small_06": .20, "WOD_Mountain_Small_07": .20, "WOD_Mountain_Base": .15,
        "WOD_Mountain_01": .55, "WOD_Mountain_02": .40, "WOD_Mountain_03": .40, "WOD_Mountain_04": .45, "WOD_Mountain_05": .30,
        "WOD_Mountain_06": .35, "WOD_Mountain_07": .36, "WOD_Mountain_08": .40, "WOD_Mountain_09": .25, "WOD_Mountain_10": .22}
f32 = np.float32

env = UnityPy.load(sys.argv[1])
tex, csvs = None, {}
for o in env.objects:
    if o.type.name == "Texture2D":
        d = o.read()
        if d.m_Name == "daggerfall_deriv_map":
            tex = d.image.convert("RGBA")       # UnityPy flips to top-down: row 0 = north (DT's "visual" rows)
    elif o.type.name == "TextAsset":
        d = o.read()
        if d.m_Name.startswith("Mountains"):
            s = d.m_Script
            csvs[d.m_Name] = s if isinstance(s, str) else s.decode("utf-8")

# Water mask: min of R over the image block of each map pixel <= 127 (float32 block bounds, as Mathf does)
r = np.asarray(tex)[:, :, 0]
ih, iw = r.shape
sx, sy = f32(iw) / f32(W), f32(ih) / f32(H)
mask = np.zeros((H, W), bool)
for y in range(H):
    top = int(np.floor(f32(y) * sy)); bot = min(ih - 1, int(np.ceil(f32(y + 1) * sy)) - 1); bot = max(bot, top)
    for x in range(W):
        c0 = int(np.floor(f32(x) * sx)); c1 = min(iw - 1, int(np.ceil(f32(x + 1) * sx)) - 1); c1 = max(c1, c0)
        mask[y, x] = r[top:bot + 1, c0:c1 + 1].min() <= 127

# Mountains: Mathf.RoundToInt = round half to even (Python round), on float32 values
lift = {}
rows = 0
for name in ("Mountains", "Mountains_Small", "Mountains_Foothills"):
    lines = [l for l in csvs[name].replace("\r", "\n").split("\n") if l]
    hdr = [h.replace('"', "").strip().lower() for h in lines[0].split(",")]
    ix = {k: hdr.index(k) for k in ("prefab", "worldx", "worldy", "terrainx", "terrainy", "scale")}
    need = max(ix.values())
    for l in lines[1:]:
        p = l.split(",")
        if len(p) <= need:
            continue
        try:
            wx, wy, tx, ty = (int(p[ix[k]]) for k in ("worldx", "worldy", "terrainx", "terrainy"))
            scale = f32(float(p[ix["scale"]]))
        except ValueError:
            continue
        ex = f32(wx) + f32(tx - 100) / f32(128)
        ey = f32(H - 1) - (f32(wy) + (f32(ty) - f32(200)) / f32(128))
        m = f32(MODS.get(p[ix["prefab"]].replace('"', "").strip(), .50))
        radius, maxlift = f32(.10) * scale, m * scale
        assert radius < 0.7, (name, l)
        nx = min(max(round(float(ex)), 0), W - 1)
        hy = min(max(round(float(ey)), 0), H - 1)
        k = (H - 1 - hy) * W + nx                      # back to map pixel y (north = 0)
        lift[k] = max(lift.get(k, f32(0)), maxlift)
        rows += 1

bits = np.packbits(mask.reshape(-1), bitorder="little")
keys = sorted(lift)
out = "Assets/StreamingAssets/VitaNative/wodfar.bin"
with open(out, "wb") as f:
    f.write(struct.pack("<i", len(keys)))
    f.write(bits.tobytes())
    for k in keys:
        f.write(struct.pack("<if", k, float(lift[k])))
print("deriv %dx%d, water cells %d, mountain rows %d -> %d lifted cells, %s %d bytes" %
      (iw, ih, mask.sum(), rows, len(keys), out, 8 + 62500 + 8 * len(keys) - 4))

if len(sys.argv) > 3:
    # Preview: Vita far heights before (basic) | after (full mode, every mountain lifted), DefaultTerrainSampler constants
    raw = open(sys.argv[2], "rb").read()
    off = struct.unpack_from("<I", raw, 28)[0]
    woods = np.frombuffer(raw, np.uint8, W * H, off).reshape(H, W).astype(np.float32)
    ocean, maxh = 3.4 * 8, 1539.0
    basic = np.maximum(woods * 12, ocean)
    full = np.where(woods * 12 < ocean, ocean + 1, woods * 12)
    lf = np.zeros(W * H, np.float32); lf[keys] = [lift[k] for k in keys]
    full = np.maximum(full, lf.reshape(H, W) * maxh)
    full[mask] = ocean
    def img(h):
        g = np.clip(h / 1539.0 * 255 * 1.5, 0, 255).astype(np.uint8)
        a = np.stack([g, g, g], -1); a[h <= ocean] = (40, 70, 120)
        return a
    Image.fromarray(np.concatenate([img(basic), img(full)], 0)).save(sys.argv[3])
    print("lifted cells above the stock height: %d of %d" % ((lf.reshape(H, W) * maxh > woods * 12).sum(), len(keys)))
