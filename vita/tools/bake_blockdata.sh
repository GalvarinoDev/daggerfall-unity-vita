#!/bin/bash
# Bakes Assets/StreamingAssets/VitaNative/blockdata.pak (Assets/Editor/Vita/VitaBlockDataBaker.cs): every BLOCKS.BSA record as
# DFU's BlocksFile.LoadBlock parses it. Usage: vita/tools/bake_blockdata.sh      ARENA2=<folder> to bake from another copy. Editor must be closed.
set -e
cd "$(dirname "$0")/../.."
A=${ARENA2:-$HOME/.local/share/Steam/steamapps/common/The Elder Scrolls Daggerfall/DF/DAGGER/ARENA2}
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
mkdir -p Assets/StreamingAssets/VitaNative
export VITA_ARENA2="$(win "$A")" VITA_BAKE_OUT="$(win Assets/StreamingAssets/VitaNative)\\blockdata.pak"
vita/unity2017.sh VitaBlockDataBaker.Bake bakeblockdata || true
grep -h "VITABAKE done\|VITABAKE failed\|VITABAKE blockdata\|error CS" ~/Games/unity2017/drive_c/2018_bakeblockdata.log | head -50
~/.venvs/unitypy/bin/python -I vita/tools/lz4pack.py Assets/StreamingAssets/VitaNative/blockdata.pak
