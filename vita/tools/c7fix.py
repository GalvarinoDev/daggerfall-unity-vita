# Regex C# 7 -> 6 rewrites, applied only to the file:line locations listed in argv[1].
import re, sys, collections
t = collections.defaultdict(set)
for loc in open(sys.argv[1]).read().splitlines():
    f, n = loc.rsplit(':', 1); t[f].add(int(n))
rules = [
  # `in` struct params -> by value
  (r'\(in (DF\w+) ', r'(\1 '), (r', in (DF\w+) ', r', \1 '),
  # ref readonly locals -> plain copies (read-only use)
  (r'\bref readonly ([\w.\[\]]+) (\w+) = ref ', r'\1 \2 = '),
  # expression-bodied accessors
  (r'\bget => ([^;{}]+);', r'get { return \1; }'),
  (r'\bset => ([^;{}]+);', r'set { \1; }'),
  # SpellCost deconstruction
  (r'\(int _, int (\w+)\) = (.+);(\s*)$', r'int \1 = \2.spellPointCost;\3'),
  (r'\(int (\w+), int (\w+)\) = (.+);(\s*)$', r'FormulaHelper.SpellCost cost = \3; int \1 = cost.goldCost, \2 = cost.spellPointCost;\4'),
  (r'\((\w+), (\w+)\) = (FormulaHelper\.\w+\(.+\));(\s*)$', r'FormulaHelper.SpellCost cost = \3; \1 = cost.goldCost; \2 = cost.spellPointCost;\4'),
]
rules = [(re.compile(a), b) for a, b in rules]
for path, nums in t.items():
    if 'BillboardBatch' in path: continue
    lines = open(path, 'rb').read().split(b'\n')
    for n in nums:
        s = lines[n-1].decode('utf-8'); new = s
        for rx, r in rules: new = rx.sub(r, new)
        lines[n-1] = new.encode('utf-8')
    open(path, 'wb').write(b'\n'.join(lines))
