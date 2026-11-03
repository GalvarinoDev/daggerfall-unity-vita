#!/bin/bash
# Bakes Assets/StreamingAssets/VitaNative/textures.pak (Assets/Editor/Vita/VitaTextureBaker.cs): every world texture of TEXTURE.000-511 (+ VitaNative 9000-9005) as the
# Vita's palette path uploads it. Usage: vita/tools/bake_textures.sh      ARENA2=<folder> to bake from another copy. Editor must be closed.
set -e
cd "$(dirname "$0")/../.."
A=${ARENA2:-$HOME/.local/share/Steam/steamapps/common/The Elder Scrolls Daggerfall/DF/DAGGER/ARENA2}
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
mkdir -p Assets/StreamingAssets/VitaNative
export VITA_ARENA2="$(win "$A")" VITA_BAKE_OUT="$(win Assets/StreamingAssets/VitaNative)\\textures.pak"
vita/unity2017.sh VitaTextureBaker.Bake baketextures || true
grep -h "VITABAKE done\|VITABAKE failed\|error CS" ~/Games/unity2017/drive_c/2018_baketextures.log
grep -c "left to the runtime path" ~/Games/unity2017/drive_c/2018_baketextures.log || true
~/.venvs/unitypy/bin/python -I vita/tools/lz4pack.py Assets/StreamingAssets/VitaNative/textures.pak
