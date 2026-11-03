#!/bin/bash
# Bakes Assets/StreamingAssets/VitaNative/strings.pak (Assets/Editor/Vita/VitaTextTableBaker.cs): every string table collection as TextProvider loads
# them. Usage: vita/tools/bake_strings.sh      Editor must be closed.
set -e
cd "$(dirname "$0")/../.."
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
mkdir -p Assets/StreamingAssets/VitaNative
export VITA_BAKE_OUT="$(win Assets/StreamingAssets/VitaNative)\\strings.pak"
vita/unity2017.sh VitaTextTableBaker.Bake bakestrings || true
grep -h "VITABAKE done\|VITABAKE failed\|VITABAKE strings\|error CS" ~/Games/unity2017/drive_c/2018_bakestrings.log
~/.venvs/unitypy/bin/python -I vita/tools/lz4pack.py Assets/StreamingAssets/VitaNative/strings.pak
