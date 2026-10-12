#!/usr/bin/env bash
# Makes the 1080p H.264 SDR video proxy that Unity's VideoPlayer plays, from a 4K HDR10 movie.
#
#   tools/make_proxy.sh "<movie.mkv>" <out.mp4>
#
# - HDR10 (PQ, BT.2020) is tonemapped to SDR BT.709 (hable), scaling to 1080p first for speed.
# - No B-frames: the MP4 then has no edit list, so frame 0 is exactly t = 0 (keeps A/V sync simple).
# - A keyframe every 24 frames (~1 s) so seeking is fast.
# - HDR mastering / light-level metadata is stripped; ffmpeg would otherwise copy it into the
#   SDR file and a decoder could treat the file as HDR.
# Phase 0: 5 minutes of RPO took about 60 s and made a 227 MB file.
set -euo pipefail
IN="${1:?usage: make_proxy.sh <movie.mkv> <out.mp4>}"
OUT="${2:?usage: make_proxy.sh <movie.mkv> <out.mp4>}"

VF="sidedata=mode=delete:type=MASTERING_DISPLAY_METADATA,sidedata=mode=delete:type=CONTENT_LIGHT_LEVEL"
VF="$VF,zscale=w=1920:h=1080:t=linear:npl=100,format=gbrpf32le,zscale=p=bt709"
VF="$VF,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p"

ffmpeg -hide_banner -y -i "$IN" -map 0:v:0 -map_metadata -1 -an -sn -vf "$VF" \
  -c:v libx264 -preset medium -crf 20 -g 24 -bf 0 \
  -color_primaries bt709 -color_trc bt709 -colorspace bt709 -movflags +faststart "$OUT"

ffprobe -v error -show_entries stream=codec_name,width,height,r_frame_rate,start_time,nb_frames,has_b_frames "$OUT"
