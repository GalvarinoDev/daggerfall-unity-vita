#!/bin/bash
# Renders the boot music (DFU's title song, song_5strong) with DFU's own synth into vita/bootmusic/build/:
# bootmusic.wav (to listen to) and bootmusic.raw (48 kHz stereo s16le, what the plugin streams).
#   vita/bootmusic/render.sh [seconds]
cd "$(dirname "$0")/../.." || exit 1
seconds=${1:-80}
mkdir -p vita/bootmusic/build
find Assets/Scripts/AudioSynthesis -name '*.cs' | sed 's/.*/"&"/' > vita/bootmusic/build/render.rsp
echo '"vita/bootmusic/RenderSong.cs"' >> vita/bootmusic/build/render.rsp
# A read-only, unlabelled mount: :Z relabels every file in the repo (that changed DFU.vpk's ctime mid-upload)
podman run --rm --security-opt label=disable -v "$PWD:/w:ro" -v "$PWD/vita/bootmusic/build:/out" -w /w docker.io/library/mono:6.12 sh -c "
  mcs -langversion:6 -nowarn:0618,0414,0649,0169,0219 -out:/tmp/RenderSong.exe @vita/bootmusic/build/render.rsp &&
  mono /tmp/RenderSong.exe Assets/Resources/TimGM6mb.sf2.bytes Assets/Resources/Songs/5strong.mid.bytes $seconds /out/bootmusic.wav /out/bootmusic.raw"
