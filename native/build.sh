#!/usr/bin/env bash
# Builds a TERN script into a standalone native binary.
#
#   native/build.sh app.tern -o app
#   native/build.sh app.tern -o app -O3
#
# Extra arguments after -o are passed to the C compiler, so -O3, -march=native
# or -flto all work.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 "$HERE/ternc.py" "$@"
