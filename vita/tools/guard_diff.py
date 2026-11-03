#!/usr/bin/env python3
# Puts every unguarded change to an upstream .cs file beside upstream's original lines:
#   replaced lines -> #if UNITY_PSP2 / ours / #else / upstream's / #endif
#   added lines    -> #if UNITY_PSP2 / ours / #endif
#   removed lines  -> #if !UNITY_PSP2 / upstream's / #endif
# Hunks that contain a preprocessor line are left alone (they are already guard edits) and listed for review.
# Each rewritten file is checked: resolving UNITY_PSP2 one way gives the current file, the other way upstream's (git HEAD).
#   python3 vita/tools/guard_diff.py [--dry-run] [files...]    (default: every modified Assets/**/*.cs)
import difflib, re, subprocess, sys

BASE = 'HEAD'   # upstream's last commit; nothing of ours is committed
DIRECTIVE = re.compile(rb'^\s*#\s*(if|elif|else|endif|define|undef|region|endregion|pragma)\b')


def lines_of(data):
    bom = data.startswith(b'\xef\xbb\xbf')
    return bom, (data[3:] if bom else data).splitlines(keepends=True)


def condition(expr, psp2):
    """True/False when UNITY_PSP2 alone decides the #if expression, None when other symbols matter (Kleene logic)."""
    expr = re.sub(rb'//.*', b'', expr).strip()
    tokens = re.findall(rb'\w+|&&|\|\||[!()]', expr)
    pos = [0]
    def peek():
        return tokens[pos[0]] if pos[0] < len(tokens) else None
    def take():
        pos[0] += 1
        return tokens[pos[0] - 1]
    def atom():
        t = take()
        if t == b'!':
            v = atom()
            return None if v is None else not v
        if t == b'(':
            v = disj()
            take()
            return v
        return psp2 if t == b'UNITY_PSP2' else {b'true': True, b'false': False}.get(t)
    def conj():
        v = atom()
        while peek() == b'&&':
            take()
            w = atom()
            v = False if v is False or w is False else (None if v is None or w is None else True)
        return v
    def disj():
        v = conj()
        while peek() == b'||':
            take()
            w = conj()
            v = True if v is True or w is True else (None if v is None or w is None else False)
        return v
    return disj()


def resolve(lines, psp2):
    """Evaluates #if blocks whose outcome UNITY_PSP2 alone decides; every other directive is kept as text."""
    out, stack = [], []          # stack entries: None (other #if) or [active_now, cond]
    def active():
        return all(s is None or s[0] for s in stack)
    for line in lines:
        t = line.strip()
        m = re.match(rb'#\s*if\b(.*)', t)
        cond = condition(m.group(1), psp2) if m and b'UNITY_PSP2' in m.group(1) else None
        if cond is not None:
            stack.append([cond, cond])
            continue
        if re.match(rb'#\s*if\b', t):
            stack.append(None)
        elif re.match(rb'#\s*else\b', t) and stack and stack[-1] is not None:
            stack[-1][0] = not stack[-1][1]
            continue
        elif re.match(rb'#\s*elif\b', t) and stack and stack[-1] is not None:
            raise ValueError('#elif after #if UNITY_PSP2')
        elif re.match(rb'#\s*endif\b', t):
            if stack.pop() is not None:
                continue
        if active():
            out.append(line)
    return out


def guard(path, dry):
    cur_data = open(path, 'rb').read()
    try:
        old_data = subprocess.run(['git', 'show', '%s:%s' % (BASE, path)], capture_output=True, check=True).stdout
    except subprocess.CalledProcessError:
        return 'new file, skipped', []
    bom, old = lines_of(old_data)
    _, cur = lines_of(cur_data)
    nl = b'\r\n' if b'\r\n' in old_data else b'\n'
    norm = lambda ls: b''.join(ls).rstrip(b'\r\n')
    def ok(out):   # both sides, end-of-file newline differences aside
        return norm(resolve(out, True)) == norm(resolve(cur, True)) and norm(resolve(out, False)) == norm(old)

    out, skipped, changed = wrap(old, cur, nl, keep_directive_hunks=True)
    if not ok(out):
        # Older guards that changed upstream lines in place: rebuild every guard from (the Vita's view, upstream's file)
        out, skipped, changed = wrap(old, resolve(cur, True), nl, keep_directive_hunks=False)
        if out is None or not ok(out):
            return 'VERIFY FAILED, left unchanged', skipped
        skipped, how = [], 'rebuilt: %d guard(s)'
    else:
        how = '%d hunk(s) guarded'
    if not changed:
        return 'nothing unguarded', skipped
    if not dry:
        open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + b''.join(out))
    return how % changed, skipped


def balanced(ls):
    depth = 0
    for l in ls:
        t = l.strip()
        if re.match(rb'#\s*if\b', t):
            depth += 1
        elif re.match(rb'#\s*endif\b', t):
            depth -= 1
            if depth < 0:
                return False
        elif re.match(rb'#\s*(else|elif)\b', t) and depth == 0:
            return False
    return depth == 0


def wrap(old, cur, nl, keep_directive_hunks):
    """Puts each difference between old (upstream) and cur beside upstream's lines. Returns (lines, skipped hunk lines, count);
    lines is None when a hunk's directives don't balance (it can't be wrapped)."""
    out, skipped, changed = [], [], 0
    def ended(ls):   # a hunk at end of file without a trailing newline needs one before the closing directive
        return ls[:-1] + [ls[-1] + nl] if ls and not ls[-1].endswith(b'\n') else ls
    for op, i1, i2, j1, j2 in difflib.SequenceMatcher(None, old, cur, autojunk=False).get_opcodes():
        o, c = old[i1:i2], cur[j1:j2]
        if op == 'equal':
            out += c
            continue
        if any(DIRECTIVE.match(l) for l in o + c):
            if keep_directive_hunks:
                skipped.append(j1 + 1)
                out += c
                continue
            if not (balanced(o) and balanced(c)):
                return None, [j1 + 1], 0
        changed += 1
        if op == 'replace':
            out += [b'#if UNITY_PSP2' + nl] + ended(c) + [b'#else' + nl] + ended(o) + [b'#endif' + nl]
        elif op == 'insert':
            out += [b'#if UNITY_PSP2' + nl] + ended(c) + [b'#endif' + nl]
        else:
            out += [b'#if !UNITY_PSP2' + nl] + ended(o) + [b'#endif' + nl]
    return out, skipped, changed


def main():
    args = sys.argv[1:]
    dry = '--dry-run' in args
    files = [a for a in args if a != '--dry-run'] or subprocess.run(
        ['git', 'diff', '--diff-filter=M', '--name-only', BASE, '--', 'Assets/*.cs'], capture_output=True, text=True).stdout.split()
    for f in files:
        result, skipped = guard(f, dry)
        print('%s: %s%s' % (f, result, ' (review lines %s)' % ', '.join(map(str, skipped)) if skipped else ''))


if __name__ == '__main__':
    main()
