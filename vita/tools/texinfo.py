#!/usr/bin/env python3
"""TEXTURE record headers -> Assets/StreamingAssets/VitaNative/texinfo.bin (runtime: Assets/Scripts/Vita/VitaTextureInfo.cs).

Enemies and town NPCs need only each record's size, scale and frame count, but DFU loads the whole TEXTURE file (often 1-3 MB) for them.
Copies those fields from Daggerfall's TEXTURE.002-511 (000/001 are solid colours with fixed sizes, not needed).
Format (little endian): "VTXI", version 1, file count, then per file: int16 archive, int16 record count, per record int16 width, height,
scaleX, scaleY, frame count. Usage: vita/tools/texinfo.py [ARENA2 folder]
"""
import os, struct, sys

arena2 = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser(
    '~/.local/share/Steam/steamapps/common/The Elder Scrolls Daggerfall/DF/DAGGER/ARENA2')
out = os.path.join(os.path.dirname(__file__), '../../Assets/StreamingAssets/VitaNative/texinfo.bin')

parts = []
for f in sorted(os.listdir(arena2)):
    name, ext = os.path.splitext(f.upper())
    if name != 'TEXTURE' or not ext[1:].isdigit() or not 2 <= int(ext[1:]) <= 511:
        continue
    data = open(os.path.join(arena2, f), 'rb').read()
    count = struct.unpack_from('<h', data, 0)[0]
    recs = []
    try:
        for r in range(count):
            pos = struct.unpack_from('<i', data, 26 + r * 20 + 2)[0]   # record header: int16 type1, int32 record position, ...
            w, h = struct.unpack_from('<hh', data, pos + 4)             # offsetX, offsetY, width, height
            frames = struct.unpack_from('<h', data, pos + 20)[0]        # ..., compression, size, data offset, isNormal, frame count
            sx, sy = struct.unpack_from('<hh', data, pos + 24)          # unknown, scaleX, scaleY
            recs.append(struct.pack('<5h', w, h, sx, sy, frames))
    except struct.error:
        print(f'texinfo: skipped {f} (truncated: DFU fails to load it too)')   # left out: the runtime takes DFU's path
        continue
    parts.append(struct.pack('<hh', int(ext[1:]), count) + b''.join(recs))
blob = b'VTXI' + struct.pack('<ii', 1, len(parts)) + b''.join(parts)
open(out, 'wb').write(blob)
print(f'texinfo: {len(parts)} files, {len(blob)} bytes -> {os.path.normpath(out)}')
