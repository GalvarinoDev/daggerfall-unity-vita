#!/usr/bin/env python3
# Compresses a Vita pack (version 1: magic, 1, count, then key / offset / length per record, records at 512-byte offsets; written by
# Assets/Editor/Vita/VitaAtlasBaker.cs "VATP" and VitaModelBaker.cs "VMDL") into version 2: each record an LZ4 block (high
# compression), index entries key / offset / stored length / raw length, records still at 512-byte offsets, shared records stay
# shared. Every block is decompressed again and compared before the file is written. Read by VitaPack (VitaLz4.Decode).
# Usage: ~/.venvs/unitypy/bin/python -I vita/tools/lz4pack.py <pack>   (in place; a version 2 pack is left alone)
import struct, sys
import lz4.block

MAGICS = {0x50544156: 'VATP', 0x4C444D56: 'VMDL', 0x58545756: 'VWTX', 0x594B5356: 'VSKY', 0x474D4956: 'VIMG', 0x46585456: 'VTXF', 0x544E4656: 'VFNT', 0x42545356: 'VSTB', 0x4B4F4256: 'VBOK', 0x54535156: 'VQST', 0x52414356: 'VCAR', 0x4F494256: 'VBIO', 0x434F4C56: 'VLOC', 0x53504D56: 'VMPS', 0x444B4256: 'VBKD', 0x4D585456: 'VTXM'}

path = sys.argv[1]
data = open(path, 'rb').read()
magic, version, count = struct.unpack_from('<iii', data, 0)
if magic not in MAGICS:
    sys.exit('not a Vita pack: ' + path)
if version == 2:
    print('lz4pack: already version 2:', path)
    sys.exit(0)
if version != 1:
    sys.exit('unknown pack version %d' % version)

entries = [struct.unpack_from('<Iii', data, 12 + 12 * i) for i in range(count)]
align = lambda n: (n + 511) & ~511
offset = align(12 + 16 * count)
blocks, placed, index = [], {}, []
for key, off, length in entries:
    if (off, length) not in placed:
        raw = data[off:off + length]
        block = lz4.block.compress(raw, mode='high_compression', compression=12, store_size=False)
        assert lz4.block.decompress(block, uncompressed_size=length) == raw
        placed[(off, length)] = (offset, len(block))
        blocks.append((offset, block))
        offset = align(offset + len(block))
    index.append((key,) + placed[(off, length)] + (length,))

out = bytearray(offset)
struct.pack_into('<iii', out, 0, magic, 2, count)
for i, entry in enumerate(index):
    struct.pack_into('<Iiii', out, 12 + 16 * i, *entry)
for off, block in blocks:
    out[off:off + len(block)] = block
open(path, 'wb').write(out)
print('lz4pack %s: %d keys, %d records, %.1f -> %.1f MB' % (MAGICS[magic], count, len(blocks), len(data) / 1e6, len(out) / 1e6))
