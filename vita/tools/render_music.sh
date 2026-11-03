#!/bin/bash
# Pre-renders DFU's MIDI songs for the Vita (no C# synth at runtime): DFU's own synth + the default SoundFont, upstream PC settings,
# into <project>/Assets/Resources/VitaMusic/song_<name>_g<gain>.wav. Unity encodes them to ATRAC9 (Assets/Editor/Vita/VitaMusicImport.cs),
# which the Vita decodes on its audio hardware. DaggerfallSongPlayer.Play loads the file matching its Gain, else falls back to the synth.
# Gain 2 = the game's SongPlayer prefab (every song); gain 5 = players made in code / the startup scene (title song only).
# Generated data (~2.9 GB of WAV): git-excluded.
# Usage: vita/tools/render_music.sh [project-dir]     (default: this repo)
set -e
cd "$(dirname "$0")/../.."
P=$(realpath "${1:-.}")
B=vita/.build/midi2wav
O="$P/Assets/Resources/VitaMusic"
mkdir -p "$B/songs" "$O"
find Assets/Scripts/AudioSynthesis -name "*.cs" > "$B/src.txt"
echo vita/tools/midi2wav/Render.cs >> "$B/src.txt"
mono() { podman run --rm -v "$PWD:/w:Z" -v "$O:/out:Z" -w /w docker.io/library/mono:6.12 "$@"; }
mono mcs -langversion:6 -optimize+ -nowarn:0618,0414,0649,0169,0219 -out:$B/Render.exe @$B/src.txt
for f in Assets/Resources/Songs/*.mid.bytes; do cp "$f" "$B/songs/$(basename "$f" .bytes)"; done
cp Assets/Resources/TimGM6mb.sf2.bytes "$B/bank.sf2"
mono mono $B/Render.exe $B/bank.sf2 2 /out _g2 $B/songs/*.mid | tail -1
mono mono $B/Render.exe $B/bank.sf2 5 /out _g5 $B/songs/5strong.mid
echo "render_music: $(ls "$O"/*.wav | wc -l) songs, $(du -sh "$O" | cut -f1) in $O"
