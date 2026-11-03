#!/bin/bash
# Applies the PS Vita player settings (Assets/Editor/Vita/VitaPlayerSettings.cs) in the real 2017.4 editor. Rerun after upstream merges.
cd "$(dirname "$0")/.." || exit 1
vita/unity2017.sh VitaPlayerSettings.Apply vitasettings
grep -h "VITASETTINGS\|error CS" ~/Games/unity2017/drive_c/2018_vitasettings.log
grep -q "VITASETTINGS" ~/Games/unity2017/drive_c/2018_vitasettings.log
