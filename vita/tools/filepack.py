#!/usr/bin/env python3
"""Small ARENA2 files -> Assets/StreamingAssets/VitaNative/arena2.pak (runtime: Assets/Scripts/Vita/VitaFilePack.cs).

The same extensions and size limit FileProxy keeps in its Vita byte cache (IMG CIF RCI COL CFG BSA BSS, <= 256 KB), stored as they are.
Format (little endian): "VSFP", version 1, count, then per file: 16-byte ASCII upper-case name (zero padded), offset, length;
data at 512-byte offsets. Usage: vita/tools/filepack.py [ARENA2 folder]
"""
import os, struct, sys

EXTS = {'.IMG', '.CIF', '.RCI', '.COL', '.CFG', '.BSA', '.BSS'}
MAX = 256 * 1024
arena2 = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser(
    '~/.local/share/Steam/steamapps/common/The Elder Scrolls Daggerfall/DF/DAGGER/ARENA2')
out = os.path.join(os.path.dirname(__file__), '../../Assets/StreamingAssets/VitaNative/arena2.pak')

files = sorted(f for f in os.listdir(arena2)
               if os.path.splitext(f)[1].upper() in EXTS and os.path.getsize(os.path.join(arena2, f)) <= MAX)
assert len(set(f.upper() for f in files)) == len(files), 'names differ only by case'
align = lambda n: (n + 511) & ~511
offset = align(12 + len(files) * 24)
index, blobs = [], []
for f in files:
    data = open(os.path.join(arena2, f), 'rb').read()
    name = f.upper().encode('ascii')
    assert len(name) <= 16, f
    index.append(struct.pack('<16sii', name, offset, len(data)))
    blobs.append((offset, data))
    offset = align(offset + len(data))
with open(out, 'wb') as o:
    o.write(b'VSFP' + struct.pack('<ii', 1, len(files)) + b''.join(index))
    for off, data in blobs:
        o.seek(off)
        o.write(data)
print(f'filepack: {len(files)} files, {offset / 1048576:.1f} MB -> {os.path.normpath(out)}')
