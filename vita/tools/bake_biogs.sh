#!/bin/bash
# Bakes Assets/StreamingAssets/VitaNative/biogs.pak (Assets/Editor/Vita/VitaBiogBaker.cs): every biography questionnaire as BiogFile parses
# them. Usage: vita/tools/bake_biogs.sh      Editor must be closed.
set -e
cd "$(dirname "$0")/../.."
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
mkdir -p Assets/StreamingAssets/VitaNative
export VITA_BAKE_OUT="$(win Assets/StreamingAssets/VitaNative)\\biogs.pak"
vita/unity2017.sh VitaBiogBaker.Bake bakebiogs || true
grep -h "VITABAKE done\|VITABAKE failed\|VITABAKE biog\|error CS" ~/Games/unity2017/drive_c/2018_bakebiogs.log
~/.venvs/unitypy/bin/python -I vita/tools/lz4pack.py Assets/StreamingAssets/VitaNative/biogs.pak
