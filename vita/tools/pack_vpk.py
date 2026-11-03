# Packs a PC Hosted Vita build folder into a VPK for a modded Vita (Linux-safe take on Unity's UnityTools.py -f -r -p).
# Usage: python3 vita/tools/pack_vpk.py [build_dir] [out.vpk]   (defaults: vita/build/DFU -> vita/build/DFU.vpk). Build folder is left untouched.
import os, sys, zipfile, zlib
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import patch_razor
src = sys.argv[1] if len(sys.argv) > 1 else 'vita/build/DFU'
out = sys.argv[2] if len(sys.argv) > 2 else src.rstrip('/') + '.vpk'
selfs = [f for f in os.listdir(src) if f.lower().endswith('.self')]
assert len(selfs) == 1, 'expected one .self in %s, found %s' % (src, selfs)
skip_top = {'SymbolFiles', 'configuration.psp2path', 'ScriptLayoutHashes.txt'}

# System images (Unity's defaults show its logo): the startup image the Vita shows before Unity runs, and the LiveArea background/gate,
# made from the user's splash art (Assets/Vita/VitaSplash.png, git-excluded) as 8-bit palette PNGs. Without it Unity's stay.
system_images = {}
splash = 'Assets/Vita/VitaSplash.png'
if os.path.exists(splash):
    import io
    from PIL import Image
    art = Image.open(splash).convert('RGB')
    def fit(w, h):  # scale to cover w x h, crop the centre
        k = max(w / art.width, h / art.height)
        im = art.resize((round(art.width * k), round(art.height * k)), Image.LANCZOS)
        x, y = (im.width - w) // 2, (im.height - h) // 2
        b = io.BytesIO()
        im.crop((x, y, x + w, y + h)).quantize(256).save(b, 'PNG', optimize=True)
        return b.getvalue()
    system_images = {'sce_sys/pic0.png': fit(960, 544), 'sce_sys/livearea/contents/bg0.png': fit(840, 500),
                     'sce_sys/livearea/contents/default_gate.png': fit(280, 158)}
# LiveArea software manual (the gate's "Manual" button): sce_sys/manual/001.png.. with no gaps, 256-colour PNGs of 600 KiB or less.
# The 1996 manual scans (vita/refs/manual/pages, git-excluded) at 544 wide (the size for scanned paper manuals, 544 x 480-960),
# with our 960x544 Vita pages (vita/refs/manual/vita_front, name order) after the cover. Cached in vita/.build/manual.
manual_pages = 'vita/refs/manual/pages'
manual_skip = set()
if os.path.isdir(manual_pages):
    from PIL import Image
    scans = sorted(os.path.join(manual_pages, f) for f in os.listdir(manual_pages)
                   if f.endswith('.png') and int(f[4:-4]) not in manual_skip)
    front = sorted(os.path.join('vita/refs/manual/vita_front', f) for f in os.listdir('vita/refs/manual/vita_front')
                   if f.endswith('.png')) if os.path.isdir('vita/refs/manual/vita_front') else []
    cache = 'vita/.build/manual'
    os.makedirs(cache, exist_ok=True)
    for n, p in enumerate(scans[:1] + front + scans[1:], 1):
        c = os.path.join(cache, p.replace('/', '_'))
        if not os.path.exists(c) or os.path.getmtime(c) < os.path.getmtime(p):
            im = Image.open(p).convert('RGB')
            if im.width != 960:  # scans: 544 wide, height to match (791 for the 871x1266 renders)
                im = im.resize((544, max(480, min(960, round(im.height * 544 / im.width)))), Image.LANCZOS)
            im.quantize(256).save(c, 'PNG', optimize=True)
        assert os.path.getsize(c) <= 600 << 10, 'manual page over 600 KiB: ' + p
        system_images['sce_sys/manual/%03d.png' % n] = open(c, 'rb').read()
# Boehm's GC_free_space_divisor in the IL2CPP module, as an offset into its data segment (changes every build): VitaMemory raises it at
# startup so the managed heap grows in small steps (the player's psp2_get_mem crashes when one big step can't be allocated)
def gc_offsets():
    import subprocess, tempfile
    d = open(os.path.join(src, 'SymbolFiles', 'il2CppAssemblies.suprx'), 'rb').read()
    elf = bytearray(d[0x1000:])          # SELF: ELF at 0x1000, its header copied at 0xa0 and program headers at 0xe0
    elf[0:52] = d[0xa0:0xa0 + 52]
    phoff, phsize, phnum = int.from_bytes(elf[28:32], 'little'), int.from_bytes(elf[42:44], 'little'), int.from_bytes(elf[44:46], 'little')
    elf[phoff:phoff + phnum * phsize] = d[0xe0:0xe0 + phnum * phsize]
    loads = [int.from_bytes(elf[phoff + i * phsize + 8:phoff + i * phsize + 12], 'little') for i in range(phnum)
             if int.from_bytes(elf[phoff + i * phsize:phoff + i * phsize + 4], 'little') == 1]
    os.makedirs('vita/.build', exist_ok=True)   # not /tmp: a RAM tmpfs that fills up ("Disk quota exceeded" lost VitaGC.txt once)
    with tempfile.NamedTemporaryFile(suffix='.elf', dir='vita/.build') as t:
        t.write(elf)
        t.flush()
        nm = subprocess.run([os.path.expanduser('~/vitasdk/bin/arm-vita-eabi-nm'), t.name], capture_output=True, text=True).stdout
    sym = [int(l.split()[0], 16) for l in nm.splitlines() if l.endswith(' GC_free_space_divisor')]
    # GC_arrays (bss, same segment): _heapsize at +0 and _large_free_bytes (wholly free heap blocks) at +16, read by VitaMemory's probes
    arrays = [int(l.split()[0], 16) for l in nm.splitlines() if l.endswith(' GC_arrays')]
    return 'GC_free_space_divisor %x\n' % (sym[0] - loads[1]) + ('GC_arrays %x\n' % (arrays[0] - loads[1]) if arrays else '')
try:
    vita_gc = gc_offsets()
except Exception as e:
    sys.exit('pack_vpk: no VitaGC.txt (%s): without it the GC divisor fix is off and big heaps crash' % e)
def method(path):
    # First-time installs unpack on the Vita's CPU: big files that barely deflate (saves under 20% on a 3 x 1 MB sample) go in stored
    size = os.path.getsize(path)
    if size < 1 << 20:
        return zipfile.ZIP_DEFLATED
    with open(path, 'rb') as f:
        packed = 0
        for at in (0, size // 2, max(0, size - (1 << 20))):
            f.seek(at)
            chunk = f.read(1 << 20)
            packed += len(zlib.compress(chunk, 1)) / float(len(chunk))
    return zipfile.ZIP_STORED if packed / 3 > 0.8 else zipfile.ZIP_DEFLATED

with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, allowZip64=True) as z:
    if vita_gc:
        z.writestr('Media/StreamingAssets/VitaGC.txt', vita_gc)
    for dirpath, dirs, files in os.walk(src):
        rel = os.path.relpath(dirpath, src)
        if rel == '.':
            dirs[:] = [d for d in dirs if d not in skip_top]
        for f in files:
            arc = f if rel == '.' else os.path.join(rel, f)
            if rel == '.' and (f in skip_top or f.lower().endswith('.bat')):
                continue
            if f == selfs[0] and rel == '.':
                data = bytearray(open(os.path.join(src, f), 'rb').read())
                data[0x80] = 0  # UnityTools "fix unsafe homebrew bug"
                print(patch_razor.patch(data))  # development player: Razor calls would crash on retail
                z.writestr('eboot.bin', bytes(data))
            elif arc.replace(os.sep, '/') in system_images:
                z.writestr(arc, system_images[arc.replace(os.sep, '/')])
            else:
                # Unity writes il2CppAssemblies.suprx but the player loads Il2CppAssemblies.suprx
                z.write(os.path.join(dirpath, f), arc.replace('il2CppAssemblies', 'Il2CppAssemblies'), compress_type=method(os.path.join(dirpath, f)))
    for arc in system_images:  # pages Unity doesn't write (the manual)
        if arc not in z.namelist():
            z.writestr(arc, system_images[arc])
print(out, os.path.getsize(out) // (1 << 20), 'MB')
