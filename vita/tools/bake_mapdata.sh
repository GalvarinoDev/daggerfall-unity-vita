#!/bin/bash
# Bakes Assets/StreamingAssets/VitaNative/maps.pak (Assets/Editor/Vita/VitaMapBaker.cs): every MAPS.BSA region and classic location and both
# PAK grids as DFU's MapsFile/PakFile read them. Usage: vita/tools/bake_mapdata.sh      ARENA2=<folder> to bake from another copy. Editor must be closed.
set -e
cd "$(dirname "$0")/../.."
A=${ARENA2:-$HOME/.local/share/Steam/steamapps/common/The Elder Scrolls Daggerfall/DF/DAGGER/ARENA2}
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
mkdir -p Assets/StreamingAssets/VitaNative
export VITA_ARENA2="$(win "$A")" VITA_BAKE_OUT="$(win Assets/StreamingAssets/VitaNative)\\maps.pak"
vita/unity2017.sh VitaMapBaker.Bake bakemapdata || true
grep -h "VITABAKE done\|VITABAKE failed\|VITABAKE map\|error CS" ~/Games/unity2017/drive_c/2018_bakemapdata.log | head -50
~/.venvs/unitypy/bin/python -I vita/tools/lz4pack.py Assets/StreamingAssets/VitaNative/maps.pak
