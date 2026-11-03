# All 320x200 (and 320x160) Daggerfall IMGs -> PNG, palettes chosen like DFU's ImgFile.PaletteName
import sys, os, struct
from PIL import Image
A, out = sys.argv[1], sys.argv[2]
def load_pal(name):
    d = open(os.path.join(A, name), 'rb').read()
    p = d[8:776] if len(d) == 776 else d[:768]
    return bytes(min(255, b * 4) for b in p) if max(p) < 64 else p
def pal_name(n):
    if n.startswith('FMAP'): return 'FMAP_PAL.COL'
    if n.startswith('NITE'): return 'NIGHTSKY.COL'
    return {'DANK02I0.IMG': 'DANKBMAP.COL', 'TMAP00I0.IMG': 'MAP.PAL'}.get(n, 'ART_PAL.COL')
done = {}
for n in sorted(os.listdir(A)):
    if not n.upper().endswith('.IMG'): continue
    d = open(os.path.join(A, n), 'rb').read()
    if len(d) == 64768: w, h, px, p = 320, 200, d[:64000], bytes(min(255, b * 4) for b in d[64000:])
    elif len(d) == 64000: w, h, px, p = 320, 200, d, load_pal(pal_name(n))
    else:
        x, y, w, h, comp, sz = struct.unpack_from('<hhhhHH', d, 0)
        if comp != 0 or w != 320 or h not in (160, 200) or len(d) < 12 + w * h: continue
        px, p = d[12:12 + w * h], load_pal(pal_name(n))
    sub = os.path.join(out, 'fullscreen_320x200' if h == 200 else 'wide_320x160')
    os.makedirs(sub, exist_ok=True)
    im = Image.frombytes('P', (w, h), px); im.putpalette(p)
    im.convert('RGB').resize((w * 2, h * 2), Image.NEAREST).save(os.path.join(sub, n.split('.')[0] + '.png'))
    done.setdefault(sub, []).append(n)
for sub, names in done.items():
    cols = 6; rows = (len(names) + cols - 1) // cols
    sheet = Image.new('RGB', (cols * 320, rows * 220), (40, 40, 40))
    from PIL import ImageDraw
    dr = ImageDraw.Draw(sheet)
    for i, n in enumerate(names):
        im = Image.open(os.path.join(sub, n.split('.')[0] + '.png')).resize((316, 200 if '200' in sub else 160))
        sheet.paste(im, ((i % cols) * 320 + 2, (i // cols) * 220)); dr.text(((i % cols) * 320 + 4, (i // cols) * 220 + 203), n, fill=(255, 255, 0))
    sheet.save(os.path.join(sub, '_sheet.png')); print(os.path.basename(sub), len(names))
