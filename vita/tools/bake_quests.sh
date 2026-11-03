#!/bin/bash
# Bakes Assets/StreamingAssets/VitaNative/quests.pak (Assets/Editor/Vita/VitaQuestBaker.cs): every quest file split as Parser.Parse splits
# them. Usage: vita/tools/bake_quests.sh      Editor must be closed.
set -e
cd "$(dirname "$0")/../.."
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
mkdir -p Assets/StreamingAssets/VitaNative
export VITA_BAKE_OUT="$(win Assets/StreamingAssets/VitaNative)\\quests.pak"
vita/unity2017.sh VitaQuestBaker.Bake bakequests || true
grep -h "VITABAKE done\|VITABAKE failed\|VITABAKE quest\|error CS" ~/Games/unity2017/drive_c/2018_bakequests.log
~/.venvs/unitypy/bin/python -I vita/tools/lz4pack.py Assets/StreamingAssets/VitaNative/quests.pak
