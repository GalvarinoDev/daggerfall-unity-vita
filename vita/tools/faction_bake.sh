#!/bin/bash
# Assets/StreamingAssets/Factions/FACTION.TXT -> Assets/StreamingAssets/VitaNative/factions.bin for FactionFile (PSP2).
# Parsed by DFU's own FactionFile.ParseFactions/ParseFactionData (copied out of FactionFile.cs, upstream branch, mono in podman):
# the ~6,000-line parse took 311 ms per boot on the Vita (s42 fac1). The ruler seeds are NOT baked: the game still draws them from
# DFRandom per faction in this order at load, as ParseFactions does, so they stay identical.
# Format: "VFAC", int32 version 1, int32 FACTION.TXT byte length (the game falls back to the text if it differs), int32 count, per
# faction in parse order: int32 id, parent, type, UTF-8 name (int32 length, -1 = null, bytes), int32 rep, summon, region, power,
# flags, ruler, ally1-3, enemy1-3, face, race, flat1, flat2, sgroup, ggroup, minf, maxf, vam, rank.
# Usage: vita/tools/faction_bake.sh
set -e
cd "$(dirname "$0")/../.."
mkdir -p vita/.build; T=$(mktemp -d -p "$PWD/vita/.build")
trap 'rm -rf "$T"' EXIT
src=Assets/Scripts/API/FactionFile.cs
cp Assets/StreamingAssets/Factions/FACTION.TXT "$T/FACTION.TXT"
cat > "$T/Bake.cs" <<'E'
using System; using System.Collections.Generic; using System.IO; using System.Text;
static class DFRandom { public static uint rand() { return 0; } public static int random_range_inclusive(int a, int b) { return 0; } }
class FactionFile {
    readonly Dictionary<int, FactionData> factionDict = new Dictionary<int, FactionData>();
    readonly Dictionary<string, int> factionNameToIDDict = new Dictionary<string, int>();
E
sed -n '/^        public struct FactionData$/,/^        }$/p' $src >> "$T/Bake.cs"
sed -n '/^        void ParseFactions(string txt)$/,/^        int ParseInt(string value)$/p' $src | sed '$d' >> "$T/Bake.cs"
cat >> "$T/Bake.cs" <<'E'
    int ParseInt(string value) { return int.Parse(value); }
    static void Str(BinaryWriter w, string s) { if (s == null) { w.Write(-1); return; } byte[] b = Encoding.UTF8.GetBytes(s); w.Write(b.Length); w.Write(b); }
    static void Main() {
        byte[] buffer = File.ReadAllBytes("FACTION.TXT");
        var f = new FactionFile();
        f.ParseFactions(Encoding.UTF8.GetString(buffer, 0, buffer.Length));
        using (var w = new BinaryWriter(File.Create("factions.bin"))) {
            w.Write(Encoding.ASCII.GetBytes("VFAC")); w.Write(1); w.Write(buffer.Length); w.Write(f.factionDict.Count);
            // Dictionary enumerates in insertion order (no removals): the order ParseFactions drew the ruler seeds in
            foreach (FactionData d in f.factionDict.Values) {
                w.Write(d.id); w.Write(d.parent); w.Write(d.type); Str(w, d.name);
                foreach (int v in new[] { d.rep, d.summon, d.region, d.power, d.flags, d.ruler, d.ally1, d.ally2, d.ally3, d.enemy1, d.enemy2,
                    d.enemy3, d.face, d.race, d.flat1, d.flat2, d.sgroup, d.ggroup, d.minf, d.maxf, d.vam, d.rank })
                    w.Write(v);
            }
        }
        Console.WriteLine("factions: {0} records, {1} names", f.factionDict.Count, f.factionNameToIDDict.Count);
    }
}
E
podman run --rm -v "$T:/w:Z" -w /w docker.io/library/mono:6.12 sh -c 'mcs -out:bake.exe Bake.cs && mono bake.exe'
cp "$T/factions.bin" Assets/StreamingAssets/VitaNative/factions.bin
ls -l Assets/StreamingAssets/VitaNative/factions.bin
