#!/bin/bash
# Bakes Assets/StreamingAssets/VitaNative/maps.bin (Assets/Editor/Vita/VitaMapIndexBaker.cs) in the 2018 editor.
# Usage: vita/tools/bake_maps.sh      ARENA2=<folder> to bake from another copy (default: the Steam one). Editor must be closed.
set -e
cd "$(dirname "$0")/../.."
A=${ARENA2:-$HOME/.local/share/Steam/steamapps/common/The Elder Scrolls Daggerfall/DF/DAGGER/ARENA2}
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
mkdir -p Assets/StreamingAssets/VitaNative
export VITA_ARENA2="$(win "$A")" VITA_BAKE_OUT="$(win Assets/StreamingAssets/VitaNative)\\maps.bin"
vita/unity2017.sh VitaMapIndexBaker.Bake bakemaps || true
grep -h "VITABAKE\|error CS" ~/Games/unity2017/drive_c/2018_bakemaps.log
