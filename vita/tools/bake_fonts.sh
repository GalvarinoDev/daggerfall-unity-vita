#!/bin/bash
# Bakes Assets/StreamingAssets/VitaNative/fonts.pak (Assets/Editor/Vita/VitaFontBaker.cs): the classic font atlases as DaggerfallFont builds
# them. Usage: vita/tools/bake_fonts.sh      Editor must be closed.
set -e
cd "$(dirname "$0")/../.."
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
mkdir -p Assets/StreamingAssets/VitaNative
export VITA_BAKE_OUT="$(win Assets/StreamingAssets/VitaNative)\\fonts.pak"
vita/unity2017.sh VitaFontBaker.Bake bakefonts || true
grep -h "VITABAKE done\|VITABAKE failed\|VITABAKE font\|error CS" ~/Games/unity2017/drive_c/2018_bakefonts.log
~/.venvs/unitypy/bin/python -I vita/tools/lz4pack.py Assets/StreamingAssets/VitaNative/fonts.pak
