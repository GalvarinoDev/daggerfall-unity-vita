#!/bin/bash
# Bakes Assets/StreamingAssets/VitaNative/skies.pak (Assets/Editor/Vita/VitaSkyBaker.cs): every classic sky frame (SKY00-31, NITE00-03)
# as DaggerfallSky uploads it. Usage: vita/tools/bake_skies.sh      ARENA2=<folder> to bake from another copy. Editor must be closed.
set -e
cd "$(dirname "$0")/../.."
A=${ARENA2:-$HOME/.local/share/Steam/steamapps/common/The Elder Scrolls Daggerfall/DF/DAGGER/ARENA2}
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
mkdir -p Assets/StreamingAssets/VitaNative
export VITA_ARENA2="$(win "$A")" VITA_BAKE_OUT="$(win Assets/StreamingAssets/VitaNative)\\skies.pak"
vita/unity2017.sh VitaSkyBaker.Bake bakeclassicsky || true
grep -h "VITABAKE done\|VITABAKE failed\|error CS" ~/Games/unity2017/drive_c/2018_bakeclassicsky.log
~/.venvs/unitypy/bin/python -I vita/tools/lz4pack.py Assets/StreamingAssets/VitaNative/skies.pak
