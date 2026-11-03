# rep(path, old, new): exact single replacement that keeps the file's BOM and CRLF/LF endings.
def rep(path, old, new, count=1):
    b = open(path, 'rb').read()
    nl = b'\r\n' if b'\r\n' in b else b'\n'
    o, n = old.encode().replace(b'\n', nl), new.encode().replace(b'\n', nl)
    found = b.count(o)
    if found != count:
        raise SystemExit(f'{path}: expected {count} match(es), found {found}:\n{old}')
    open(path, 'wb').write(b.replace(o, n))

def guard(path, old, cond='!UNITY_PSP2'):
    """Wrap an exact block of whole lines in #if cond / #endif."""
    rep(path, old, f'#if {cond}\n{old}\n#endif')

def _method(path, signature):
    b = open(path, 'rb').read()
    nl = b'\r\n' if b'\r\n' in b else b'\n'
    lines = b.split(nl)
    hits = [k for k, l in enumerate(lines) if signature.encode() in l]
    if len(hits) != 1:
        raise SystemExit(f'{path}: {len(hits)} lines match {signature!r}')
    s = i = hits[0]
    while not lines[i].strip().startswith(b'{'): i += 1
    depth, j = 0, i
    while True:
        depth += lines[j].count(b'{') - lines[j].count(b'}')
        if depth == 0: break
        j += 1
    return nl, lines, s, i, j   # signature line, opening brace line, closing brace line

def guard_body(path, signature, cond='!UNITY_PSP2', else_=None):
    """Wrap a method body (brace-matched) in #if; optional #else lines (e.g. 'return false;')."""
    nl, lines, s, i, j = _method(path, signature)
    tail = [b'#else'] + [l.encode() for l in else_.split('\n')] if else_ else []
    lines[j:j] = tail + [b'#endif']
    lines[i+1:i+1] = [f'#if {cond}'.encode()]
    open(path, 'wb').write(nl.join(lines))

def guard_method(path, signature, cond='!UNITY_PSP2'):
    """Wrap a whole method (signature line .. closing brace) in #if."""
    nl, lines, s, i, j = _method(path, signature)
    lines[j+1:j+1] = [b'#endif']
    lines[s:s] = [f'#if {cond}'.encode()]
    open(path, 'wb').write(nl.join(lines))
