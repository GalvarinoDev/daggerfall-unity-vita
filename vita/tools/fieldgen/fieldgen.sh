#!/bin/bash
# Regenerates Assets/Scripts/Vita/VitaFieldsGen.cs from the gate's compiled Assembly-CSharp.dll (run vita/check_all.sh first). Needs podman.
set -e
cd "$(dirname "$0")/../../.."
mkdir -p vita/.build; T=$(mktemp -d -p "$PWD/vita/.build"); trap 'rm -rf "$T"' EXIT
cp vita/.build/Assembly-CSharp.dll vita/tools/fieldgen/FieldGen.cs "$T"/; cp vita/refs/psp2/*.dll "$T"/ 2>/dev/null || true
podman run --rm -v "$T:/w:Z" -w /w docker.io/library/mono:6.12 sh -c 'mcs -out:g.exe FieldGen.cs && mono g.exe Assembly-CSharp.dll out.cs DaggerfallConnect.DFBlock DaggerfallConnect.DFLocation DaggerfallConnect.DFRegion DaggerfallConnect.DFCareer'
cp "$T/out.cs" Assets/Scripts/Vita/VitaFieldsGen.cs
