#!/usr/bin/env bash
# Builds a few TERN programs for Windows with the Zig cross compiler and checks
# the result is a real x86-64 console executable. There is no Windows machine
# and no Wine here, so this proves the build, not the behaviour; run the .exe on
# Windows to see it print.
#
#   pip install ziglang     # provides the compiler this script looks for
set -u

HERE="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

ZIG="${TERN_ZIG:-}"
if [ -z "$ZIG" ]; then
    ZIG="$(command -v zig || true)"
fi
if [ -z "$ZIG" ]; then
    ZIG="$(python3 - <<'PY' 2>/dev/null
import os, ziglang
print(os.path.join(os.path.dirname(ziglang.__file__), "zig"))
PY
)"
fi
if [ -z "$ZIG" ] || [ ! -x "$ZIG" ]; then
    echo "windows: no Zig found (pip install ziglang, or set TERN_ZIG) — skipped"
    exit 0
fi

cd "$HERE"
echo "windows: using $ZIG"

status=0
for name in 01_basics 02_strings 05_collections 08_errors 09_builtins 22_fs 27_json 26_net; do
    source="tests/$name.tern"
    [ -f "$source" ] || continue
    if ! TERN_ZIG="$ZIG" python3 native/ternc.py --target windows "$source" -o "$WORK/$name.exe" > "$WORK/$name.log" 2>&1; then
        echo "windows: $name did not build" >&2
        sed -n 1,20p "$WORK/$name.log" >&2
        status=1
        continue
    fi
    if ! python3 - "$WORK/$name.exe" "$name" <<'PY'
import struct, sys

path, name = sys.argv[1], sys.argv[2]
data = open(path, "rb").read()
assert data[:2] == b"MZ", f"{name}: not an MZ executable"
pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
assert data[pe_offset:pe_offset + 4] == b"PE\0\0", f"{name}: no PE header"
machine = struct.unpack_from("<H", data, pe_offset + 4)[0]
optional = pe_offset + 24
magic = struct.unpack_from("<H", data, optional)[0]
assert magic == 0x20B, f"{name}: not a PE32+ image"
subsystem = struct.unpack_from("<H", data, optional + 68)[0]
assert machine == 0x8664, f"{name}: machine {machine:#x} is not x86-64"
assert subsystem == 3, f"{name}: subsystem {subsystem} is not a console program"
assert b"KERNEL32.dll" in data, f"{name}: does not link the Windows kernel library"
print(f"windows: {name}.exe ok ({len(data) // 1024} KiB, x86-64 console)")
PY
    then
        status=1
    fi
done

if [ "$status" = "0" ]; then
    echo "windows: builds verified"
fi
exit "$status"
