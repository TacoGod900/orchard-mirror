#!/usr/bin/env bash
set -euo pipefail

export PATH="/ucrt64/bin:/usr/bin:${PATH}"

script \
  -q \
  -f \
  /c/Users/techb/Downloads/Orchard/artifacts/uxplay-live.log \
  -c '/c/tmp/orchard-mirror-feasibility/third_party/UxPlay/build/uxplay.exe -n "Orchard Mirror" -nh -p 7100 -avdec -vs d3d11videosink -vsync no -as wasapisink -nc -d 1'
