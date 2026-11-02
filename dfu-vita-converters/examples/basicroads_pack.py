# Basic Roads (Hazelnut, MIT) as its own mod pack, like the other built-in mods ("DLC" layers, loaded only when switched on):
# the mod's four 500 KB path maps (one direction byte per map pixel, 94-96% zeros) -> Assets/StreamingAssets/VitaMods/basicroads.pak,
# ~66 KB instead of 2 MB in Resources. Same format as generic/worldpack.py (VWPK version 2, raw deflate), entries of kind 4 (raw bytes)
# named as the mod's files; VitaWorldPack.ReadRaw reads them for BasicRoadsTexturing.
#   python3 -I examples/basicroads_pack.py <dir with roadData.bytes, trackData.bytes, riverData.bytes, streamData.bytes>
import os, struct, sys, zlib

src = sys.argv[1]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Assets", "StreamingAssets", "VitaMods", "basicroads.pak")
entries = []
for name in ("roadData.bytes", "trackData.bytes", "riverData.bytes", "streamData.bytes"):
    raw = open(os.path.join(src, name), "rb").read()
    assert len(raw) == 1000 * 500, name  # MapsFile.MaxMapPixelX * MaxMapPixelY
    c = zlib.compressobj(9, zlib.DEFLATED, -15)
    blob = c.compress(raw) + c.flush()
    assert zlib.decompress(blob, -15) == raw
    entries.append((name.encode("utf-8"), blob, len(raw)))
head = bytearray(b"VWPK" + struct.pack("<ii", 2, len(entries)))
offset = len(head) + sum(4 + len(n) + 16 for n, _, _ in entries)
for n, blob, size in entries:
    head += struct.pack("<i", len(n)) + n + struct.pack("<iiii", 4, offset, len(blob), size)
    offset += len(blob)
with open(out, "wb") as f:
    f.write(head)
    for e in entries:
        f.write(e[1])
print("%s: %d entries, %d bytes" % (out, len(entries), offset))
