#!/usr/bin/env python3
# Lets Unity 2018.2's PS Vita *development* player run on a retail Vita (for the Unity Profiler).
# The dev player imports 15 sceRazor* functions (Razor GPU capture/HUD, ScePerf CPU markers) as weak imports. Those modules exist
# only on dev kits, so on retail the calls crash at PC 0 during GXM init (UNITY_VITA_GUIDE §4). The player is a relocatable
# module (ELF type 0xFE04): every call loads the stub address with movw/movt, and the loader writes that address from an SCE
# relocation (symbol segment 0 + addend). This rewrites the addend of every relocation that targets a sceRazor* stub to a leaf
# function of the player that returns -1 (what an unresolved stub would return), so Unity sees "Razor not available".
# Usage: patch_razor.py <DFU.self> (in place), or patch(bytearray) from pack_vpk.py. A release player has no Razor stubs: no change.
import struct, sys

TARGET = "UNITY_ft_validator_run"           # movs.w r0, #-1; bx lr (Thumb)
TARGET_BYTES = bytes.fromhex("5ff0ff307047")
# The CPU markers and GXMRazorHud's per-frame calls (BeginFrame/PresentFrame/Control: GpuLive*, GpuTrace*, HudReloadSettings,
# GpuPerfGetMode) run every frame, and the dev player logs "SCE error detected: 0xffffffff" in its on-screen console for every
# negative result except 0x8008C002 (run2018v/w): they return 0 instead (IsCapturing 0 = not capturing). The one-time init calls
# (GpuCaptureEnableSalvage, CaptureSetTriggerNextFrame) keep -1.
ZERO = "gray_raster_set_mode"               # movs r0, #0; bx lr (Thumb)
ZERO_BYTES = bytes.fromhex("00207047")
ZERO_STUBS = ("sceRazorCpuPushMarkerWithHud", "sceRazorCpuPopMarker", "sceRazorCpuSync", "sceRazorCpuIsCapturing",
              "sceRazorGpuLiveSetBuffer", "sceRazorGpuLiveSetMetricsGroup", "sceRazorGpuLiveStart", "sceRazorGpuLiveStop",
              "sceRazorGpuTraceSetMetricsGroup", "sceRazorGpuTraceSetFilename", "sceRazorGpuTraceTrigger",
              "sceRazorHudReloadSettings", "sceRazorGpuPerfGetMode")
ELF = 0x1000                                # the ELF image inside an unencrypted SELF


def patch(data):
    """Patches the SELF in data (bytearray) in place; returns a one-line report. Raises ValueError if the player doesn't fit."""
    if data[ELF:ELF + 4] != b"\x7fELF":
        raise ValueError("patch_razor: no ELF at 0x1000")
    e_type, = struct.unpack_from("<H", data, ELF + 16)
    phoff, shoff = struct.unpack_from("<II", data, ELF + 28)
    phnum, = struct.unpack_from("<H", data, ELF + 44)
    shnum, = struct.unpack_from("<H", data, ELF + 48)
    if e_type != 0xFE04:
        raise ValueError("patch_razor: ELF type 0x%x is not a relocatable SCE module" % e_type)
    phdrs = [struct.unpack_from("<8I", data, ELF + phoff + i * 32) for i in range(phnum)]
    shdrs = [struct.unpack_from("<10I", data, ELF + shoff + i * 40) for i in range(shnum)]

    # Symbols: the sceRazor* stubs and the -1 leaf function
    symtab = next(s for s in shdrs if s[1] == 2)
    strtab = shdrs[symtab[6]]
    stubs, target, zero = {}, None, None
    for i in range(symtab[5] // 16):
        name_off, value, size, info, other, shndx = struct.unpack_from("<IIIBBH", data, ELF + symtab[4] + i * 16)
        end = data.index(b"\0", ELF + strtab[4] + name_off)
        name = data[ELF + strtab[4] + name_off:end].decode()
        if name.startswith("sceRazor") and info & 0xf == 2:
            stubs[value] = name               # symbol values are segment-0 offsets in this module
        elif name == TARGET and info & 0xf == 2 and target is None:
            target = value & ~1
        elif name == ZERO and info & 0xf == 2 and zero is None:
            zero = value & ~1
    if not stubs:
        return "patch_razor: no sceRazor stubs (release player): nothing to do"
    if target is None or data[ELF + phdrs[0][1] + target:ELF + phdrs[0][1] + target + 6] != TARGET_BYTES:
        raise ValueError("patch_razor: %s missing or not 'return -1' in this player" % TARGET)
    if zero is None or data[ELF + phdrs[0][1] + zero:ELF + phdrs[0][1] + zero + 4] != ZERO_BYTES:
        raise ValueError("patch_razor: %s missing or not 'return 0' in this player" % ZERO)

    # Only the code's relocations (.rela.text): the import tables' own relocations (.rela.sceLib.stub, .rela.sceFStub...)
    # tell the loader where the stubs are and must stay
    shstr = shdrs[struct.unpack_from("<H", data, ELF + 50)[0]]
    def section_name(sh):
        start = ELF + shstr[4] + sh[0]
        return data[start:data.index(b"\0", start)].decode()
    patched = {}
    for p in [sh for sh in shdrs if section_name(sh) == ".rela.text"]:
        pos, end = ELF + p[4], ELF + p[4] + p[5]
        while pos < end:
            w0, = struct.unpack_from("<I", data, pos)
            fmt = w0 & 0xf
            symseg = (w0 >> 4) & 0xf
            if fmt == 0:                    # long: w0, addend, offset
                addend_at, size = pos + 4, 12
            elif fmt == 2:                  # w0, addend
                addend_at, size = pos + 4, 8
            elif fmt in (1, 3):             # 22-bit addends: can't reach the stubs (offset > 0x3fffff)
                addend_at, size = None, 8
            elif fmt in (4, 5, 6, 7, 8, 9):
                addend_at, size = None, 4
            else:
                raise ValueError("patch_razor: unknown relocation format %d at 0x%x" % (fmt, pos - ELF))
            if addend_at is not None and symseg == 0:
                addend, = struct.unpack_from("<I", data, addend_at)
                if addend in stubs:
                    struct.pack_into("<I", data, addend_at, (zero if stubs[addend] in ZERO_STUBS else target) | 1)  # Thumb
                    patched[stubs[addend]] = patched.get(stubs[addend], 0) + 1
            pos += size
    missing = sorted(set(stubs.values()) - set(patched))
    return "patch_razor: %d relocations -> %s / %s for the per-frame calls (%s)%s" % (sum(patched.values()), TARGET, ZERO,
        ", ".join("%s x%d" % kv for kv in sorted(patched.items())),
        "; no relocation found for: " + ", ".join(missing) if missing else "")


def main(path):
    data = bytearray(open(path, "rb").read())
    print(patch(data))
    open(path, "wb").write(data)


if __name__ == "__main__":
    main(sys.argv[1])
