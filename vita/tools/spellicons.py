#!/usr/bin/env python3
"""Spell icon packs (Assets/StreamingAssets/SpellIcons/*.png) -> Assets/StreamingAssets/VitaNative/spellicons-<pack>.bin.

Runtime: SpellIconCollection.VitaTryLoadBakedPack (PSP2). Cuts the atlas as SpellIconCollection.TryLoadPackIcons does (rowCount icons per
row, square icons, from the top-left) and stores each icon as raw RGBA32 rows bottom-up (LoadRawTextureData layout).
Format: dim, count (int32 LE), then count * dim * dim * 4 bytes. Usage: vita/tools/spellicons.py
"""
import glob, json, os, struct
from PIL import Image

root = os.path.join(os.path.dirname(os.path.abspath(__file__)), '../../Assets/StreamingAssets')
for png in sorted(glob.glob(os.path.join(root, 'SpellIcons', '*.png'))):
    key = os.path.splitext(os.path.basename(png))[0]
    meta = json.load(open(os.path.splitext(png)[0] + '.txt', encoding='utf-8-sig'))
    atlas = Image.open(png).convert('RGBA')
    dim = atlas.width // meta['rowCount']
    assert atlas.width % dim == 0 and atlas.height % dim == 0, key
    out = os.path.join(root, 'VitaNative', 'spellicons-%s.bin' % key)
    with open(out, 'wb') as o:
        o.write(struct.pack('<ii', dim, meta['iconCount']))
        for i in range(meta['iconCount']):
            x, y = (i % meta['rowCount']) * dim, (i // meta['rowCount']) * dim
            o.write(atlas.crop((x, y, x + dim, y + dim)).transpose(Image.FLIP_TOP_BOTTOM).tobytes())
    print('spellicons: %s %d icons %dx%d -> %s' % (key, meta['iconCount'], dim, dim, os.path.normpath(out)))
