#!/usr/bin/env bash
# Launches TERN Studio.
#
#   bash tools/ide.sh                 open the repository
#   bash tools/ide.sh examples        open a directory
#   bash tools/ide.sh --screenshot x.png   render once and exit (headless)
#
# Requirements: Python 3 with PySide6, and the TERN toolchain
# (tools/setup-toolchain.sh). On a machine with a display Qt uses it; with none,
# pass --screenshot, which switches Qt to its offscreen platform.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$HERE"

PYTHON="${TERN_PYTHON:-}"
if [[ -z "$PYTHON" ]]; then
  for candidate in "$HERE/.venv/bin/python" "/tmp/qtenv/bin/python"; do
    if [[ -x "$candidate" ]] && "$candidate" -c "import PySide6" 2>/dev/null; then
      PYTHON="$candidate"
      break
    fi
  done
fi

if [[ -z "$PYTHON" ]]; then
  for candidate in python3 python; do
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c "import PySide6" 2>/dev/null; then
      PYTHON="$candidate"
      break
    fi
  done
fi

if [[ -z "$PYTHON" ]]; then
  cat >&2 <<'EOF'
TERN Studio needs PySide6:

    python3 -m venv .venv
    .venv/bin/pip install "PySide6-Essentials==6.9.3"

Then run this script again.
EOF
  exit 1
fi

# Qt needs libGL/libEGL/libxkbcommon/libdbus-1 to load, even when it never
# calls them. On a headless machine only the offscreen platform is used, and
# stand-ins for those four are enough; build them if they are missing.
if [[ "$*" == *--screenshot* || "$*" == *--offscreen* ]]; then
  export QT_QPA_PLATFORM="${QT_QPA_PLATFORM:-offscreen}"
  if [[ ! -d build/qt-stubs ]]; then
    "$PYTHON" tools/ide/qt_stubs.py >/dev/null 2>&1 || true
  fi
  if [[ -d build/qt-stubs ]]; then
    export LD_LIBRARY_PATH="$HERE/build/qt-stubs${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
  fi
fi

export DOTNET_ROOT="${TERN_TOOLCHAIN_DIR:-/tmp/tern-toolchain}/dotnet"

# Run and Debug are part of the IDE, so all three assemblies are built together
# on a first launch rather than failing later with "run tools/build.py".
missing=0
for assembly in TERN.Runner.dll TERN.Ide.dll TERN.Debug.dll; do
  [[ -f "build/$assembly" ]] || missing=1
done
if [[ $missing -eq 1 ]]; then
  echo "building the runner, syntax server and debugger..." >&2
  tools/build.py --all >&2 || exit 1
fi

# The project directory is the one argument that is not a flag or a flag's
# value; if it is missing, the app falls back to its remembered project.
positional=()
skip=0
for argument in "$@"; do
  if [[ $skip -eq 1 ]]; then skip=0; continue; fi
  case "$argument" in
    --theme|--screenshot|--size) skip=1 ;;
    -*) ;;
    *) positional+=("$argument") ;;
  esac
done

if [[ ${#positional[@]} -gt 1 ]]; then
  echo "usage: tools/ide.sh [project-directory] [--theme darcula|light] [--screenshot FILE] [--size 1440x900]" >&2
  exit 2
fi

exec "$PYTHON" -m ide.Studio "$@"
