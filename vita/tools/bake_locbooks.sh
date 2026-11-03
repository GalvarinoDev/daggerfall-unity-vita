#!/bin/bash
# Bakes Assets/StreamingAssets/VitaNative/locbooks.pak (Assets/Editor/Vita/VitaLocBookBaker.cs): every -LOC book text as LocalizedBook parses
# them. Usage: vita/tools/bake_locbooks.sh      Editor must be closed.
set -e
cd "$(dirname "$0")/../.."
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
mkdir -p Assets/StreamingAssets/VitaNative
export VITA_BAKE_OUT="$(win Assets/StreamingAssets/VitaNative)\\locbooks.pak"
vita/unity2017.sh VitaLocBookBaker.Bake bakelocbooks || true
grep -h "VITABAKE done\|VITABAKE failed\|VITABAKE locbook\|error CS" ~/Games/unity2017/drive_c/2018_bakelocbooks.log
~/.venvs/unitypy/bin/python -I vita/tools/lz4pack.py Assets/StreamingAssets/VitaNative/locbooks.pak
