#!/bin/bash
# Bakes Assets/StreamingAssets/VitaNative/texfiles.pak (Assets/Editor/Vita/VitaTextureFileBaker.cs): every TEXTURE.### record and frame as DFU's TextureFile
# hands it out. Usage: vita/tools/bake_texfiles.sh      ARENA2=<folder> to bake from another copy. Editor must be closed.
set -e
cd "$(dirname "$0")/../.."
A=${ARENA2:-$HOME/.local/share/Steam/steamapps/common/The Elder Scrolls Daggerfall/DF/DAGGER/ARENA2}
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
mkdir -p Assets/StreamingAssets/VitaNative
export VITA_ARENA2="$(win "$A")" VITA_BAKE_OUT="$(win Assets/StreamingAssets/VitaNative)\\texfiles.pak"
vita/unity2017.sh VitaTextureFileBaker.Bake baketexfiles || true
grep -h "VITABAKE done\|VITABAKE failed\|VITABAKE texfile\|error CS" ~/Games/unity2017/drive_c/2018_baketexfiles.log
~/.venvs/unitypy/bin/python -I vita/tools/lz4pack.py Assets/StreamingAssets/VitaNative/texfiles.pak
