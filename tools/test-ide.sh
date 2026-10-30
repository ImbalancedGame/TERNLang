#!/usr/bin/env bash
# Drives the syntax server over stdio the way an editor would and checks the
# answers. Nothing here inspects the implementation: it sends LSP messages and
# asserts on the JSON that comes back, so the server is tested through the same
# door the IDE uses.
#
#   bash tools/test-ide.sh
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$HERE"

if [[ ! -f build/TERN.Ide.dll ]]; then
    tools/build.py --ide >/dev/null 2>&1 || { echo "ide: build failed (run tools/build.py --ide)" >&2; exit 1; }
fi

exec python3 tools/ide/lsp_client.py "$@"
