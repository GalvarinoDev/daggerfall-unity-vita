# Hoist C# 7 inline `out T name` declarations to a `T name;` line before the statement.
# Only touches the file:line locations given on stdin (from mcs errors). Prints lines it skips.
import re, sys, collections

OUT = re.compile(r'\bout ((?:[A-Za-z_]\w*\.)*[A-Za-z_]\w*(?:<[^<>]+>)?(?:\[\])?) ([A-Za-z_]\w*)\b')
SKIP_TYPES = {'var', 'ref', 'in'}

targets = collections.defaultdict(set)
for loc in sys.stdin.read().split():
    f, n = loc.rsplit(':', 1)
    targets[f].add(int(n))

for path, nums in targets.items():
    raw = open(path, 'rb').read()
    lines = raw.split(b'\n')
    out = []
    for n, bline in enumerate(lines, 1):
        line = bline.decode('utf-8')
        ms = [m for m in OUT.finditer(line) if m.group(1) not in SKIP_TYPES]
        if n not in nums or not ms:
            out.append(bline); continue
        code = line.lstrip('﻿ \t')
        if code.startswith(('else', '//', '/', '*')) or '=>' in line or not re.match(r'(if|while|var|\w[\w.<>\[\]]* \w+ =|\w[\w.]*\(|\w+ =|return)\b', code):
            print(f'MANUAL {path}:{n}: {code.strip()}'); out.append(bline); continue
        indent = re.match(r'﻿?([ \t]*)', line).group(1)
        cr = '\r' if line.endswith('\r') else ''
        for m in ms:
            out.append(f'{indent}{m.group(1)} {m.group(2)};{cr}'.encode('utf-8'))
        out.append(OUT.sub(lambda m: m.group(0) if m.group(1) in SKIP_TYPES else f'out {m.group(2)}', line).encode('utf-8'))
    open(path, 'wb').write(b'\n'.join(out))
