#!/bin/bash
# Assets/Resources/NameGen.txt (DFU's name generator banks, JSON) -> Assets/StreamingAssets/VitaNative/namegen.bin for NameHelper (PSP2).
# Parsed by DFU's own FullSerializer DLL (mono in podman), so its leniency (upstream's file has a missing and a trailing comma) is kept:
# parsing the 28 KB JSON took 285-543 ms on the Vita at the first city load.
# Format: "VNMG", int32 version 1, int32 bank count, per bank: int32 BankTypes value, int32 setCount, int32 set count, per set:
# int32 setIndex, int32 part count, per part: int32 UTF-8 length, bytes. Null arrays/strings are written as length -1.
# Usage: vita/tools/namegen_bake.sh
set -e
cd "$(dirname "$0")/../.."
mkdir -p vita/.build; T=$(mktemp -d -p "$PWD/vita/.build")
trap 'rm -rf "$T"' EXIT
cp Assets/Resources/NameGen.txt "$T/NameGen.txt"
cp "Assets/Resources/FullSerializer - Unity.dll" "$T/"
# FullSerializer's Unity converters need UnityEngine's types to load (never called here): the local reference DLLs, in the temp dir only
cp vita/refs/psp2-2018/UnityEngine*.dll "$T/"
# BankTypes exactly as NameHelper declares it (FullSerializer reads the keys by name)
sed -n '/public enum BankTypes/,/}/p' Assets/Scripts/Game/Utility/NameHelper.cs > "$T/enum.txt"
cat > "$T/Bake.cs" <<'E'
using System; using System.Collections.Generic; using System.IO; using System.Text; using FullSerializer;
static class Bake {
E
cat "$T/enum.txt" >> "$T/Bake.cs"
cat >> "$T/Bake.cs" <<'E'
    public struct NameBank { public int setCount; public NameSet[] sets; }
    public struct NameSet { public int setIndex; public string[] parts; }
    static void Main() {
        object result = null;
        new fsSerializer().TryDeserialize(fsJsonParser.Parse(File.ReadAllText("NameGen.txt")), typeof(Dictionary<BankTypes, NameBank>), ref result).AssertSuccessWithoutWarnings();
        var banks = (Dictionary<BankTypes, NameBank>)result;
        int parts = 0;
        using (var w = new BinaryWriter(File.Create("namegen.bin"))) {
            w.Write(Encoding.ASCII.GetBytes("VNMG")); w.Write(1); w.Write(banks.Count);
            foreach (var kv in banks) {
                w.Write((int)kv.Key); w.Write(kv.Value.setCount); w.Write(kv.Value.sets == null ? -1 : kv.Value.sets.Length);
                if (kv.Value.sets == null) continue;
                foreach (var set in kv.Value.sets) {
                    w.Write(set.setIndex); w.Write(set.parts == null ? -1 : set.parts.Length);
                    if (set.parts == null) continue;
                    foreach (var p in set.parts) {
                        if (p == null) { w.Write(-1); continue; }
                        byte[] b = Encoding.UTF8.GetBytes(p); w.Write(b.Length); w.Write(b); parts++;
                    }
                }
            }
        }
        Console.WriteLine("namegen: {0} banks, {1} parts", banks.Count, parts);
    }
}
E
podman run --rm -v "$T:/w:Z" -w /w docker.io/library/mono:6.12 sh -c \
  'mcs "-r:FullSerializer - Unity.dll" -out:bake.exe Bake.cs && mono bake.exe'
cp "$T/namegen.bin" Assets/StreamingAssets/VitaNative/namegen.bin
ls -l Assets/StreamingAssets/VitaNative/namegen.bin
