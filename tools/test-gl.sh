#!/usr/bin/env bash
# Exercises std:gl with no GPU and no driver: tools/gl/mock_driver.c is built as
# a libEGL that answers the same calls, then the probe runs in the interpreter
# and as a compiled binary and the two outputs must match. The PNG the probe
# writes is decoded afterwards, so the read-back and the writer are checked too.
set -u

HERE="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

cd "$HERE"

echo "gl: building the mock driver in $WORK"
cc -shared -fPIC -o "$WORK/libEGL.so.1" tools/gl/mock_driver.c || {
    echo "gl: cannot build the mock driver" >&2
    exit 1
}

echo "gl: running the probe in the interpreter"
TERN_GL_LIBRARY="$WORK/libEGL.so.1" TERN_GL_PNG="$WORK/interp.png" \
    ./tools/tern tools/gl/probe.tern > "$WORK/interp.txt" 2>&1 || {
    echo "gl: the interpreter probe failed" >&2
    cat "$WORK/interp.txt" >&2
    exit 1
}

echo "gl: compiling the probe natively"
python3 native/ternc.py tools/gl/probe.tern -o "$WORK/probe" > "$WORK/build.txt" 2>&1 || {
    echo "gl: the native build failed" >&2
    cat "$WORK/build.txt" >&2
    exit 1
}

TERN_GL_LIBRARY="$WORK/libEGL.so.1" TERN_GL_PNG="$WORK/native.png" \
    "$WORK/probe" > "$WORK/native.txt" 2>&1 || {
    echo "gl: the native probe failed" >&2
    cat "$WORK/native.txt" >&2
    exit 1
}

if ! diff -u "$WORK/interp.txt" "$WORK/native.txt"; then
    echo "gl: the two back ends disagree" >&2
    exit 1
fi
echo "gl: both back ends agree"

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
    raw = zlib.decompress(idat)
    return size, raw

for name in ("interp", "native"):
    (width, height, depth, colour), raw = read_png(f"{sys.argv[1]}/{name}.png")
    assert (width, height, depth, colour) == (4, 3, 8, 6), (width, height, depth, colour)
    stride = width * 4 + 1
    rows = [raw[i * stride:(i + 1) * stride] for i in range(height)]
    assert all(row[0] == 0 for row in rows), "unexpected filter byte"
    pixels = {tuple(row[1 + x * 4:5 + x * 4]) for row in rows for x in range(width)}
    assert (255, 0, 0, 255) in pixels, "the drawn colour is missing"
    assert (64, 128, 191, 255) in pixels, "the clear colour is missing"
print("gl: the PNG decodes and holds the drawn and cleared colours")
PY

echo "gl: the unavailable path"
cat > "$WORK/missing.tern" <<'TERN'
import "std:gl";
println(gl.available());
try {
    gl.create(4, 4);
} catch (e) {
    println(str(e));
}
TERN
TERN_GL_LIBRARY=/nonexistent/libEGL.so.1 ./tools/tern "$WORK/missing.tern" > "$WORK/missing_interp.txt" 2>&1
python3 native/ternc.py "$WORK/missing.tern" -o "$WORK/missing" > /dev/null 2>&1
TERN_GL_LIBRARY=/nonexistent/libEGL.so.1 "$WORK/missing" > "$WORK/missing_native.txt" 2>&1
if ! diff -u "$WORK/missing_interp.txt" "$WORK/missing_native.txt"; then
    echo "gl: the two back ends disagree when the driver is missing" >&2
    exit 1
fi
cat "$WORK/missing_interp.txt"
echo "gl: ok"
