#!/bin/bash
# Runs a static editor method (from Assets/Editor/Vita) in the real Unity 2018.2.19f1 editor (Proton) on this project, PSP2 target.
# Usage: vita/unity2017.sh <Class.Method> <logname>    Log: ~/Games/unity2017/drive_c/2018_<logname>.log
cd "$(dirname "$0")/.." || exit 1
export WINEPREFIX=~/Games/unity2017 PROTONPATH=~/.local/share/Steam/compatibilitytools.d/GE-Proton11-7-x86_64 GAMEID=umu-default
. vita/local.env || exit 1  # machine-specific paths (not in git)
timeout 3600 umu-run 'C:\Unity2018\Editor\Unity.exe' -batchmode -nographics -quit -buildTarget PSP2 \
  -projectPath "Z:$(pwd | tr / '\\')" -executeMethod "$1" -logFile "C:\\2018_$2.log" >/dev/null 2>&1
