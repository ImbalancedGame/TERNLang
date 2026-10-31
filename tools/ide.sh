#!/usr/bin/env bash
# Launches TERN Studio. The logic lives in tools/studio.py so that Windows
# (tools\ide.cmd) and Unix run the same code; this is just the way in.
#
#   bash tools/ide.sh                      open the repository
#   bash tools/ide.sh examples             open a directory
#   bash tools/ide.sh --screenshot x.png   render once and exit (headless)
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 "$HERE/studio.py" "$@"
