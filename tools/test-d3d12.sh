#!/usr/bin/env bash
# Exercises std:d3d12 with no Windows and no GPU.
#
# tools/d3d12/mock_d3d12.c is built as a shared library with the same exported
# entry points and the same COM vtables as d3d12.dll, so the runtime's calls land
# on the right slots. tools/d3d12/mock_user32.c is the same trick for the window:
# user32, gdi32 and kernel32 in one library, so d3d12.window() opens, presents and
# closes without a display. The probe runs in the interpreter and as a compiled
# binary, the two outputs must match, and the PNG is decoded afterwards. Finally
# the same probe is cross-compiled for Windows with Zig, which proves the
# Direct3D 12 and Win32 code compiles against the real headers as well as the
# fake ones.
set -u

HERE="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

cd "$HERE"

echo "d3d12: building the mock driver"
cc -shared -fPIC -o "$WORK/libd3d12.so" tools/d3d12/mock_d3d12.c || {
    echo "d3d12: cannot build the mock driver" >&2
    exit 1
}
export TERN_D3D12_LIBRARY="$WORK/libd3d12.so"
export TERN_DXGI_LIBRARY="$WORK/libd3d12.so"
# The mock exports D3DCompile too, so the shader path runs without HLSL.
export TERN_D3DCOMPILER_LIBRARY="$WORK/libd3d12.so"

echo "d3d12: building the mock window"
cc -shared -fPIC -o "$WORK/libuser32.so" tools/d3d12/mock_user32.c || {
    echo "d3d12: cannot build the mock window" >&2
    exit 1
}
export TERN_USER32_LIBRARY="$WORK/libuser32.so"
export TERN_GDI32_LIBRARY="$WORK/libuser32.so"
export TERN_KERNEL32_LIBRARY="$WORK/libuser32.so"

# A barrier that names the wrong "before" state is undefined behaviour on real
# hardware; the driver may remove the device and the failure then surfaces at the
# next call that touches it. The mock tracks every resource's state and says so,
# and any such line fails this script.
check_states() {
    if grep -q "STATE MISMATCH" "$1"; then
        echo "d3d12: a barrier named the wrong resource state" >&2
        grep "STATE MISMATCH" "$1" | head -5 >&2
        exit 1
    fi
    # A render target created without the colour it will be cleared to makes
    # every clear ordered against the old contents, and the debug layer on a
    # real machine says so. The same run fails here.
    if grep -q "CLEAR VALUE" "$1"; then
        echo "d3d12: a render target has the wrong optimized clear value" >&2
        grep "CLEAR VALUE" "$1" | head -5 >&2
        exit 1
    fi
    if grep -q "COPY ARGUMENT MISMATCH" "$1"; then
        echo "d3d12: a copy call does not match the documented signature" >&2
        grep "COPY ARGUMENT MISMATCH" "$1" | head -5 >&2
        exit 1
    fi
}

echo "d3d12: running the probe in the interpreter"
TERN_D3D12_PNG="$WORK/interp.png" ./tools/tern tools/d3d12/probe.tern \
    > "$WORK/interp.out" 2> "$WORK/interp.err" || {
    echo "d3d12: the interpreter probe failed" >&2
    cat "$WORK/interp.out" "$WORK/interp.err" >&2
    exit 1
}
check_states "$WORK/interp.out"
check_states "$WORK/interp.err"

echo "d3d12: compiling the probe natively"
python3 native/ternc.py tools/d3d12/probe.tern -o "$WORK/probe" > "$WORK/build.txt" 2>&1 || {
    echo "d3d12: the native build failed" >&2
    cat "$WORK/build.txt" >&2
    exit 1
}

TERN_D3D12_PNG="$WORK/native.png" "$WORK/probe" > "$WORK/native.out" 2> "$WORK/native.err" || {
    echo "d3d12: the native probe failed" >&2
    cat "$WORK/native.out" "$WORK/native.err" >&2
    exit 1
}
check_states "$WORK/native.out"
check_states "$WORK/native.err"

if ! diff -u "$WORK/interp.out" "$WORK/native.out"; then
    echo "d3d12: the two back ends disagree" >&2
    exit 1
fi
echo "d3d12: both back ends agree"

# A window may not appear unless a program asks for one: on a machine that has a
# display, a test run that quietly opened a window would be a bug in the test.
grep -q "window: unavailable" "$WORK/interp.out" || {
    echo "d3d12: the probe opened a window without being asked to" >&2
    grep "window" "$WORK/interp.out" >&2
    exit 1
}
echo "d3d12: no window is opened unless one is asked for"

python3 - "$WORK" <<'PY'
import struct, sys, zlib

def read_png(path):
    data = open(path, "rb").read()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", "not a PNG"
    offset, idat, size = 8, b"", None
    while offset < len(data):
        length, kind = struct.unpack(">I4s", data[offset:offset + 8])
        body = data[offset + 8:offset + 8 + length]
        if kind == b"IHDR":
            width, height, depth, colour, _, _, _ = struct.unpack(">IIBBBBB", body)
            size = (width, height, depth, colour)
        elif kind == b"IDAT":
            idat += body
        offset += 12 + length
    return size, zlib.decompress(idat)

for name in ("interp", "native"):
    (width, height, depth, colour), raw = read_png(f"{sys.argv[1]}/{name}.png")
    assert (width, height, depth, colour) == (3, 2, 8, 6), (width, height, depth, colour)
    stride = width * 4 + 1
    pixels = [tuple(raw[i * stride + 1 + x * 4:i * stride + 5 + x * 4]) for i in range(height) for x in range(width)]
    # The probe clears, then draws a triangle whose first vertex is red: the mock
    # paints the target with that colour, so both have to show up.
    assert (255, 0, 0, 255) in set(pixels), set(pixels)
print("d3d12: the PNG decodes to the drawn colour")
PY

# ------------------------------------------------------------------ the window
#
# The same probe with a window: TERN_D3D12_WINDOW=1 lets the mock hand back a
# window, and TERN_WINDOW_FRAMES=2 makes it close itself after two frames, which
# is what a person closing it would do. Both back ends have to present the same
# frames - the checksums are over the pixels that reached the window, so they
# catch a flipped image or a wrong channel order in either implementation.
echo "d3d12: presenting frames through the mock window"
export TERN_D3D12_WINDOW=1
export TERN_WINDOW_FRAMES=2

TERN_D3D12_PNG="$WORK/interp_window.png" ./tools/tern tools/d3d12/probe.tern \
    > "$WORK/win_interp.out" 2> "$WORK/win_interp.err" || {
    echo "d3d12: the interpreter probe failed with a window" >&2
    cat "$WORK/win_interp.out" "$WORK/win_interp.err" >&2
    exit 1
}
TERN_D3D12_PNG="$WORK/native_window.png" "$WORK/probe" \
    > "$WORK/win_native.out" 2> "$WORK/win_native.err" || {
    echo "d3d12: the native probe failed with a window" >&2
    cat "$WORK/win_native.out" "$WORK/win_native.err" >&2
    exit 1
}
check_states "$WORK/win_interp.out"

grep -q "window: open" "$WORK/win_interp.out" || {
    echo "d3d12: the probe did not open a window when asked" >&2
    cat "$WORK/win_interp.out" >&2
    exit 1
}
if [ "$(grep -c 'mock_user32: frame' "$WORK/win_interp.err")" != 2 ]; then
    echo "d3d12: the interpreter did not present two frames" >&2
    cat "$WORK/win_interp.err" >&2
    exit 1
fi
if ! diff -u <(grep 'mock_user32: frame' "$WORK/win_interp.err") \
             <(grep 'mock_user32: frame' "$WORK/win_native.err"); then
    echo "d3d12: the two back ends put different pixels in the window" >&2
    exit 1
fi
if ! diff -u "$WORK/win_interp.out" "$WORK/win_native.out"; then
    echo "d3d12: the two back ends disagree about the window" >&2
    exit 1
fi
echo "d3d12: both back ends presented the same frames"

# The rotating cube drives the same code with five renderers and far more
# transitions than the probe, so it gets its own turn.
echo "d3d12: rendering the cube through the same mock"
python3 native/ternc.py examples/rotating_cube.tern -o "$WORK/cube" > "$WORK/cube_build.txt" 2>&1 || {
    echo "d3d12: the cube did not build" >&2
    sed -n 1,20p "$WORK/cube_build.txt" >&2
    exit 1
}
if ! TERN_D3D12_PNG="$WORK/cube.png" "$WORK/cube" --frames 2 --out "$WORK/cube_frames" \
        > "$WORK/cube.txt" 2>&1; then
    echo "d3d12: the cube failed through the mock" >&2
    cat "$WORK/cube.txt" >&2
    exit 1
fi
check_states "$WORK/cube.txt"
grep -q "with d3d12" "$WORK/cube.txt" || {
    echo "d3d12: the cube did not use the Direct3D 12 renderer" >&2
    cat "$WORK/cube.txt" >&2
    exit 1
}
echo "d3d12: the cube keeps every resource state consistent"

# And the cube in a window, in both back ends: the thing a person sees when they
# run it on Windows, minus the window.
echo "d3d12: animating the cube in the mock window"
export TERN_WINDOW_FRAMES=5
"$WORK/cube" > "$WORK/cube_window.out" 2> "$WORK/cube_window.err" || {
    echo "d3d12: the cube failed in a window" >&2
    cat "$WORK/cube_window.out" "$WORK/cube_window.err" >&2
    exit 1
}
./tools/tern examples/rotating_cube.tern > "$WORK/cube_window_interp.out" \
    2> "$WORK/cube_window_interp.err" || {
    echo "d3d12: the interpreter could not animate the cube in a window" >&2
    cat "$WORK/cube_window_interp.out" "$WORK/cube_window_interp.err" >&2
    exit 1
}
for stream in "$WORK/cube_window.err" "$WORK/cube_window_interp.err"; do
    if [ "$(grep -c 'mock_user32: frame' "$stream")" != 5 ]; then
        echo "d3d12: the cube did not present five frames ($stream)" >&2
        cat "$stream" >&2
        exit 1
    fi
done
if ! grep -q "window closed." "$WORK/cube_window.out"; then
    echo "d3d12: the cube did not report the window closing" >&2
    cat "$WORK/cube_window.out" >&2
    exit 1
fi
if ! diff -u "$WORK/cube_window_interp.out" "$WORK/cube_window.out"; then
    echo "d3d12: the two back ends disagree about the cube's window" >&2
    exit 1
fi
echo "d3d12: the cube animates in both back ends"
unset TERN_D3D12_WINDOW

# ------------------------------------------------------------------ diagnose
#
# The program a user runs on a machine this repository cannot reach. Under the
# mock it proves the whole path prints one line per call, in both back ends.
echo "d3d12: running the diagnostic program"
unset TERN_D3D12_WINDOW
TERN_D3D12_PNG="$WORK/diag_interp.png" ./tools/tern examples/d3d12_diagnose.tern \
    > "$WORK/diag_interp.out" 2> "$WORK/diag_interp.err" || {
    echo "d3d12: the interpreter diagnostic failed" >&2
    cat "$WORK/diag_interp.out" "$WORK/diag_interp.err" >&2
    exit 1
}
python3 native/ternc.py examples/d3d12_diagnose.tern -o "$WORK/diagnose" > "$WORK/diag_build.txt" 2>&1 || {
    echo "d3d12: the diagnostic did not build" >&2
    sed -n 1,20p "$WORK/diag_build.txt" >&2
    exit 1
}
TERM_D3D12_PNG="$WORK/diag_native.png" TERN_D3D12_PNG="$WORK/diag_native.png" "$WORK/diagnose" \
    > "$WORK/diag_native.out" 2> "$WORK/diag_native.err" || {
    echo "d3d12: the native diagnostic failed" >&2
    cat "$WORK/diag_native.out" "$WORK/diag_native.err" >&2
    exit 1
}
# The file sizes differ between the two runs only if the writers do, and they
# are compared, so any difference is a real one.
sed -i "s|$WORK/diag_interp.png|diag.png|g" "$WORK/diag_interp.out"
sed -i "s|$WORK/diag_native.png|diag.png|g" "$WORK/diag_native.out"
if ! diff -u "$WORK/diag_interp.out" "$WORK/diag_native.out"; then
    echo "d3d12: the two back ends disagree about the diagnostic" >&2
    exit 1
fi
grep -q "^binding: " "$WORK/diag_interp.out" || {
    echo "d3d12: the diagnostic does not report the binding build" >&2
    cat "$WORK/diag_interp.out" >&2
    exit 1
}
if grep -q ": .*Error" "$WORK/diag_interp.out"; then
    echo "d3d12: the diagnostic reported an error against the mock" >&2
    cat "$WORK/diag_interp.out" >&2
    exit 1
fi
grep -q "^done$" "$WORK/diag_interp.out" || {
    echo "d3d12: the diagnostic did not finish" >&2
    cat "$WORK/diag_interp.out" >&2
    exit 1
}
echo "d3d12: the diagnostic walks the whole path in both back ends"

# ------------------------------------------------------- a removed device
#
# The failure that started this was reported three calls after the cause. These
# two runs prove the message now says which call broke the device, and that the
# interpreter and the compiled binary word it identically.
echo "d3d12: a device removed during a submit"
for backend in interp native; do
    # A panic goes to stderr, so the two streams are captured together: what a
    # user pastes is what the terminal showed.
    if [ "$backend" = "native" ]; then
        TERN_D3D12_REMOVED_AFTER=1 "$WORK/probe" > "$WORK/removed_$backend.out" 2>&1 || true
    else
        TERN_D3D12_REMOVED_AFTER=1 ./tools/tern tools/d3d12/probe.tern \
            > "$WORK/removed_$backend.out" 2>&1 || true
    fi
done
if ! diff -u "$WORK/removed_interp.out" "$WORK/removed_native.out"; then
    echo "d3d12: the two back ends disagree about a removed device" >&2
    exit 1
fi
grep -q "the device was removed during d3d12.clear: the GPU hung" "$WORK/removed_interp.out" || {
    echo "d3d12: a removed device does not name the call that broke it" >&2
    cat "$WORK/removed_interp.out" >&2
    exit 1
}
echo "d3d12: the report names the call that removed the device"

echo "d3d12: a device that fails the readback map"
for backend in interp native; do
    if [ "$backend" = "native" ]; then
        TERN_D3D12_MAP_REMOVED=1 "$WORK/probe" > "$WORK/map_$backend.out" 2>&1 || true
    else
        TERN_D3D12_MAP_REMOVED=1 ./tools/tern tools/d3d12/probe.tern \
            > "$WORK/map_$backend.out" 2>&1 || true
    fi
done
if ! diff -u "$WORK/map_interp.out" "$WORK/map_native.out"; then
    echo "d3d12: the two back ends disagree about a failed map" >&2
    exit 1
fi
grep -q "the readback buffer could not be mapped: the GPU hung and the driver reset it (TDR) (0x887a0005)" \
    "$WORK/map_interp.out" || {
    echo "d3d12: a failed map does not say why" >&2
    cat "$WORK/map_interp.out" >&2
    exit 1
}
echo "d3d12: the report says why the map failed"

# ------------------------------------------------------------- the debug layer
# The layer is what makes a failure on somebody else's machine actionable: the
# driver names the illegal call instead of the device quietly disappearing. The
# mock stores a message of its own when asked to, so the query, the message
# structure, the size negotiation, the severity and the clearing all run.
echo "d3d12: the debug layer reports what the driver stored"
TERN_D3D12_INVALID_CALL=1 TERN_D3D12_PNG="$WORK/dbg.png" ./tools/tern tools/d3d12/probe.tern \
    > "$WORK/dbg_interp.out" 2> "$WORK/dbg_interp.err" || {
    echo "d3d12: the interpreter probe failed with validation on" >&2
    cat "$WORK/dbg_interp.out" "$WORK/dbg_interp.err" >&2
    exit 1
}
TERN_D3D12_INVALID_CALL=1 TERN_D3D12_PNG="$WORK/dbg.png" "$WORK/probe" \
    > "$WORK/dbg_native.out" 2> "$WORK/dbg_native.err" || {
    echo "d3d12: the native probe failed with validation on" >&2
    cat "$WORK/dbg_native.out" "$WORK/dbg_native.err" >&2
    exit 1
}
for stream in "$WORK/dbg_interp.err" "$WORK/dbg_native.err"; do
    if [ "$(grep -c 'd3d12 debug: ERROR: ID3D12CommandList::CopyTextureRegion' "$stream")" -lt 1 ]; then
        echo "d3d12: the stored validation message did not reach the program ($stream)" >&2
        cat "$stream" >&2
        exit 1
    fi
done
if ! diff -u <(grep '^d3d12 debug:' "$WORK/dbg_interp.err") \
             <(grep '^d3d12 debug:' "$WORK/dbg_native.err"); then
    echo "d3d12: the two back ends report validation differently" >&2
    exit 1
fi
if ! diff -u "$WORK/dbg_interp.out" "$WORK/dbg_native.out"; then
    echo "d3d12: validation changed the two back ends' output" >&2
    exit 1
fi
echo "d3d12: both back ends print the driver's own words"

echo "d3d12: the unavailable path"
cat > "$WORK/missing.tern" <<'TERN'
import "std:d3d12";
println(d3d12.available());
try {
    d3d12.create(4, 4);
} catch (e) {
    println(str(e));
}
TERN
TERN_D3D12_LIBRARY="$WORK/absent.so" ./tools/tern "$WORK/missing.tern" > "$WORK/missing_interp.txt" 2>&1
python3 native/ternc.py "$WORK/missing.tern" -o "$WORK/missing" > /dev/null 2>&1
TERN_D3D12_LIBRARY="$WORK/absent.so" "$WORK/missing" > "$WORK/missing_native.txt" 2>&1
if ! diff -u "$WORK/missing_interp.txt" "$WORK/missing_native.txt"; then
    echo "d3d12: the two back ends disagree when the library is missing" >&2
    exit 1
fi
cat "$WORK/missing_interp.txt"

# The Windows build compiles the same code against the real d3d12.h when the
# cross compiler is around, which catches a structure or signature that only the
# mock would have accepted.
ZIG="${TERN_ZIG:-$(command -v zig || true)}"
if [ -z "$ZIG" ]; then
    # pip's ziglang may live in the checkout's virtualenv rather than the system
    # interpreter, which is where this repository installs it.
    for candidate in python3 "$HERE/.venv/bin/python" /tmp/qtenv/bin/python; do
        [ -x "$candidate" ] || command -v "$candidate" >/dev/null 2>&1 || continue
        ZIG="$("$candidate" - <<'ZIGPY' 2>/dev/null
import os, ziglang
print(os.path.join(os.path.dirname(ziglang.__file__), "zig"))
ZIGPY
)"
        [ -n "$ZIG" ] && break
    done
fi
if [ -n "$ZIG" ] && [ -x "$ZIG" ]; then
    echo "d3d12: checking the structure mirror against the real d3d12.h"
    python3 tools/d3d12/gen_layout_check.py > "$WORK/layout_check.c"
    if ! "$ZIG" cc -target x86_64-windows-gnu -c -o "$WORK/layout_check.o" "$WORK/layout_check.c" 2> "$WORK/layout.txt"; then
        echo "d3d12: the mirror does not match the Windows SDK headers" >&2
        sed -n 1,20p "$WORK/layout.txt" >&2
        exit 1
    fi
    echo "d3d12: the mirror matches d3d12.h"

    echo "d3d12: checking the window structures against the real windows.h"
    python3 tools/d3d12/gen_window_layout_check.py > "$WORK/window_layout.c"
    if ! "$ZIG" cc -target x86_64-windows-gnu -c -o "$WORK/window_layout.o" "$WORK/window_layout.c" 2> "$WORK/window_layout.txt"; then
        echo "d3d12: the window structures do not match windows.h" >&2
        sed -n 1,20p "$WORK/window_layout.txt" >&2
        exit 1
    fi
    echo "d3d12: the window structures match windows.h"

    echo "d3d12: cross-compiling the probe for Windows"
    if ! python3 native/ternc.py --target windows tools/d3d12/probe.tern -o "$WORK/probe.exe" > "$WORK/win.txt" 2>&1; then
        echo "d3d12: the Windows build failed" >&2
        sed -n 1,20p "$WORK/win.txt" >&2
        exit 1
    fi
    python3 - "$WORK/probe.exe" <<'PY'
import struct, sys
data = open(sys.argv[1], "rb").read()
pe = struct.unpack_from("<I", data, 0x3C)[0]
assert data[pe:pe + 4] == b"PE\0\0" and struct.unpack_from("<H", data, pe + 4)[0] == 0x8664
print("d3d12: Windows build ok (x86-64 console)")
PY
else
    echo "d3d12: no Zig, skipping the Windows build"
fi

echo "d3d12: ok"
