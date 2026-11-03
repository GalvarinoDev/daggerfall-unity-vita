#!/usr/bin/env python3
# C# 7 -> 6: hoists out-variable declarations at call sites ("f(out Type x)") into "Type x;" before the statement and passes "out x".
# Discards ("out Type _") get a numbered dummy. Method signatures are left alone. Statement start: the line itself, or for a line
# continuing a condition ("&&", "||", ")"), the nearest line above that starts with if/while/else if/return/bool/var.
#   python3 vita/tools/outvarfix.py <file.cs>...
import re, sys
OUT = re.compile(r"\bout ((?:[A-Z]\w*(?:\.[A-Z]\w*)*(?:<[^()]*?>)?)|int|float|bool|string|ulong|long) (\w+)\b")
DECL = re.compile(r"\s*(?:(?:public|private|protected|internal|static|override|virtual)\s+)*[\w.]+(?:<[\w<>, .]*>)?(?:\[\])?\s+\w+\s*")
KW = {"if", "while", "return", "else", "foreach", "for", "new", "using", "switch"}
class SIG:
    @staticmethod
    def match(s):  # a method or local function declaration: "<modifiers> Type Name(" with no keyword in front
        head = s.split("(", 1)[0]
        return "(" in s and DECL.fullmatch(head) is not None and head.split()[0] not in KW
STMT = re.compile(r"^\s*(if|while|else if|return|bool|var|foreach)\b")
for path in sys.argv[1:]:
    L = open(path, encoding="utf-8").read().split("\n")
    n = 0; i = 0
    while i < len(L):
        s = L[i]
        if "out " in s and not SIG.match(s) and OUT.search(s):
            decls = []
            def rep(m):
                global n
                t, v = m.group(1), m.group(2)
                if t == "var": return m.group(0)
                if v == "_":
                    n += 1; v = "vitaDiscard%d" % n
                decls.append("%s %s;" % (t, v)); return "out " + v
            L[i] = OUT.sub(rep, s)
            j = i
            if not STMT.match(L[j]) and re.match(r"^\s*(&&|\|\||\))", L[j]):
                while j > 0 and not STMT.match(L[j]): j -= 1
            ind = re.match(r"^\s*", L[j]).group(0)
            for d in reversed(decls): L.insert(j, ind + d)
            i += len(decls)
        i += 1
    open(path, "w", encoding="utf-8").write("\n".join(L))
