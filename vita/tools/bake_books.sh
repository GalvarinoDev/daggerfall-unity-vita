#!/bin/bash
# Bakes Assets/StreamingAssets/VitaNative/books.pak (Assets/Editor/Vita/VitaBookBaker.cs): every classic BOOKS/BOK file as DFU's BookFile
# reads it. Usage: vita/tools/bake_books.sh      ARENA2=<folder> to bake from another copy. Editor must be closed.
set -e
cd "$(dirname "$0")/../.."
A=${ARENA2:-$HOME/.local/share/Steam/steamapps/common/The Elder Scrolls Daggerfall/DF/DAGGER/ARENA2}
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
mkdir -p Assets/StreamingAssets/VitaNative
export VITA_ARENA2="$(win "$A")" VITA_BAKE_OUT="$(win Assets/StreamingAssets/VitaNative)\\books.pak"
vita/unity2017.sh VitaBookBaker.Bake bakebooks || true
grep -h "VITABAKE done\|VITABAKE failed\|VITABAKE book\|error CS" ~/Games/unity2017/drive_c/2018_bakebooks.log
~/.venvs/unitypy/bin/python -I vita/tools/lz4pack.py Assets/StreamingAssets/VitaNative/books.pak
