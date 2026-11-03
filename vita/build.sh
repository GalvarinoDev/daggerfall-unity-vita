#!/bin/bash
# PC Hosted PS Vita build in the real 2018.2.19 editor. Output: vita/build/DFU. Log: ~/Games/unity2017/drive_c/2018_vitabuild.log
# Unity reports shader compile errors as build errors; the build still completes, so pass/fail is the output files plus shader errors.
cd "$(dirname "$0")/.." || exit 1
rm -rf vita/build/DFU
vita/unity2017.sh VitaBuild.Build vitabuild
log=~/Games/unity2017/drive_c/2018_vitabuild.log
grep -h "VITABUILD\|error CS\|Error building" "$log"
grep -o "Shader error in '[^']*'" "$log" | sort | uniq -c
# No shader errors expected: shaders the Vita can't run (ColorBoost, RetroPalettization) have #pragma exclude_renderers psp2
[ -f vita/build/DFU/DFU.self ] && [ -f vita/build/DFU/Media/Modules/il2CppAssemblies.suprx ] &&
  ! grep -q "Shader error in" "$log"
