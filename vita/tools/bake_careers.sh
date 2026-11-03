#!/bin/bash
# Bakes Assets/StreamingAssets/VitaNative/careers.pak (Assets/Editor/Vita/VitaCareerBaker.cs): every CLASS*.CFG and MONSTER.BSA career as DFU's ClassFile/MonsterFile
# structure it. Usage: vita/tools/bake_careers.sh      ARENA2=<folder> to bake from another copy. Editor must be closed.
set -e
cd "$(dirname "$0")/../.."
A=${ARENA2:-$HOME/.local/share/Steam/steamapps/common/The Elder Scrolls Daggerfall/DF/DAGGER/ARENA2}
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
mkdir -p Assets/StreamingAssets/VitaNative
export VITA_ARENA2="$(win "$A")" VITA_BAKE_OUT="$(win Assets/StreamingAssets/VitaNative)\\careers.pak"
vita/unity2017.sh VitaCareerBaker.Bake bakecareers || true
grep -h "VITABAKE done\|VITABAKE failed\|VITABAKE career\|error CS" ~/Games/unity2017/drive_c/2018_bakecareers.log
~/.venvs/unitypy/bin/python -I vita/tools/lz4pack.py Assets/StreamingAssets/VitaNative/careers.pak
