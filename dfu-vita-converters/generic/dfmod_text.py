# Text assets of a .dfmod (world-data JSON etc.): count and total size by kind, or extract them all.
#   python3 -I generic/dfmod_text.py <mod.dfmod> [<out dir>]
import collections, os, sys, UnityPy
env = UnityPy.load(sys.argv[1])
out = sys.argv[2] if len(sys.argv) > 2 else None
kinds, sizes = collections.Counter(), collections.Counter()
for o in env.objects:
    if o.type.name != "TextAsset":
        continue
    t = o.read()
    data = t.m_Script if isinstance(t.m_Script, bytes) else t.m_Script.encode("utf-8", "surrogateescape")
    k = "location" if t.m_Name.startswith("location") else ("building" if "-building" in t.m_Name else ("block" if ".RMB" in t.m_Name else "other"))
    kinds[k] += 1; sizes[k] += len(data)
    if out:
        os.makedirs(out, exist_ok=True)
        open(os.path.join(out, t.m_Name + ".json"), "wb").write(data)
for k in kinds:
    print("  %-9s %5d files %7.1f MB" % (k, kinds[k], sizes[k] / 1e6))
