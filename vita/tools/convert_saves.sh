#!/bin/bash
# Converts saves between PC DFU (SAVEn folders of JSON) and the Vita (SAVEn.vita files): Assets/Editor/Vita/VitaSaveConverter.cs.
# Usage: vita/tools/convert_saves.sh topc|tovita <in-folder> <out-folder>     (editor must be closed; the inputs are only read)
set -e
cd "$(dirname "$0")/../.."
case "$1" in topc) m=ToPC ;; tovita) m=ToVita ;; *) echo "usage: $0 topc|tovita <in> <out>"; exit 1 ;; esac
[ -d "$2" ] || { echo "no folder $2"; exit 1; }
mkdir -p "$3"
win() { echo "Z:$(realpath "$1" | tr / '\\')"; }
export VITA_SAVE_IN="$(win "$2")" VITA_SAVE_OUT="$(win "$3")"
vita/unity2017.sh VitaSaveConverter.$m convertsaves || true
grep -h "VITASAVECONV\|error CS" ~/Games/unity2017/drive_c/2018_convertsaves.log
