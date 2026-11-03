#!/usr/bin/env python3
# Exact-text edits that keep each file's BOM and line endings: edit_exact.py <edits.py>, where edits.py defines
# EDITS = [(path, old, new), ...]; every old must occur exactly once (in LF form), or nothing is written.
import sys, runpy
edits = runpy.run_path(sys.argv[1])['EDITS']
files = {}
for path, old, new in edits:
    if path not in files:
        raw = open(path, 'rb').read()
        files[path] = [raw.startswith(b'\xef\xbb\xbf'), b'\r\n' in raw, raw.decode('utf-8-sig').replace('\r\n', '\n')]
    text = files[path][2]
    n = text.count(old)
    if n != 1:
        sys.exit('edit_exact: %s: old text found %d times:\n%s' % (path, n, old[:300]))
    files[path][2] = text.replace(old, new, 1)
for path, (bom, crlf, text) in files.items():
    if crlf:
        text = text.replace('\n', '\r\n')
    open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + text.encode('utf-8'))
print('edit_exact: %d edits in %d files' % (len(edits), len(files)))
