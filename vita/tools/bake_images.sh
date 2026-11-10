#!/bin/bash
# Bakes Assets/StreamingAssets/VitaNative/images.pak (Assets/Editor/Vita/VitaImageBaker.cs): every IMG/CIF/RCI/CFA/BSS/GFX file as DFU's
# readers decode it. Usage: vita/tools/bake_images.sh      ARENA2=<folder> to bake from another copy. Editor must be closed.
set -e
cd "$(dirname "$0")/../.."
A=${ARENA2:-$HOME/.local/share/Steam/steamapps/common/The Elder Scrolls Daggerfall/DF/DAGGER/ARENA2}
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
mkdir -p Assets/StreamingAssets/VitaNative
export VITA_ARENA2="$(win "$A")" VITA_BAKE_OUT="$(win Assets/StreamingAssets/VitaNative)\\images.pak"
vita/unity2017.sh VitaImageBaker.Bake bakeimages || true
grep -h "VITABAKE done\|VITABAKE failed\|VITABAKE image\|VITABAKE language\|error CS" ~/Games/unity2017/drive_c/2018_bakeimages.log
~/.venvs/unitypy/bin/python -I vita/tools/lz4pack.py Assets/StreamingAssets/VitaNative/images.pak
