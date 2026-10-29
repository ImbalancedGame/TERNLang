#!/usr/bin/env bash
# Builds the reference interpreters used by tools/compare.sh:
#   Lua 5.4 and LuaJIT 2.1 (C), Node.js (already installed), and the C# baseline
#   (compiled with the same Roslyn toolchain as TERN).
#
# Everything lands in build/langs and is cached, so this is safe to re-run.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CACHE="$HERE/build/langs"
mkdir -p "$CACHE"

have() { command -v "$1" >/dev/null 2>&1; }

# ---------------------------------------------------------------- Lua 5.4
if [[ ! -x "$CACHE/lua/bin/lua" ]]; then
  if have gcc; then
    echo "building Lua 5.4..."
    mkdir -p "$CACHE/lua"
    curl -sL -o "$CACHE/lua.tar.gz" https://codeload.github.com/lua/lua/tar.gz/refs/heads/v5.4
    rm -rf "$CACHE/lua/src"
    mkdir -p "$CACHE/lua/src"
    tar xzf "$CACHE/lua.tar.gz" -C "$CACHE/lua/src" --strip-components=1
    # no readline in the sandbox; the interpreter works fine without it
    sed -i 's/-DLUA_USE_LINUX -DLUA_USE_READLINE/-DLUA_USE_LINUX/' "$CACHE/lua/src/makefile"
    sed -i 's/^MYLIBS=.*/MYLIBS= -ldl/' "$CACHE/lua/src/makefile"
    make -C "$CACHE/lua/src" -j"$(nproc)" >/dev/null 2>&1
    mkdir -p "$CACHE/lua/bin"
    cp "$CACHE/lua/src/lua" "$CACHE/lua/bin/lua"
    rm -f "$CACHE/lua.tar.gz"
  else
    echo "warning: gcc missing, skipping Lua" >&2
  fi
fi

# ---------------------------------------------------------------- LuaJIT
if [[ ! -x "$CACHE/luajit/bin/luajit" ]]; then
  if have gcc; then
    echo "building LuaJIT 2.1..."
    mkdir -p "$CACHE/luajit"
    curl -sL -o "$CACHE/luajit.tar.gz" https://codeload.github.com/LuaJIT/LuaJIT/tar.gz/refs/heads/v2.1
    rm -rf "$CACHE/luajit/src"
    mkdir -p "$CACHE/luajit/src"
    tar xzf "$CACHE/luajit.tar.gz" -C "$CACHE/luajit/src" --strip-components=1
    make -C "$CACHE/luajit/src" -j"$(nproc)" >/dev/null 2>&1
    mkdir -p "$CACHE/luajit/bin"
    cp "$CACHE/luajit/src/src/luajit" "$CACHE/luajit/bin/luajit"
    rm -f "$CACHE/luajit.tar.gz"
  else
    echo "warning: gcc missing, skipping LuaJIT" >&2
  fi
fi

# ---------------------------------------------------------------- C# baseline
if [[ ! -f "$HERE/build/csharp-bench/Bench.dll" ]]; then
  python3 "$HERE/tools/build-csharp.py"
fi

for tool in "$CACHE/lua/bin/lua" "$CACHE/luajit/bin/luajit"; do
  [[ -x "$tool" ]] && echo "ready: $tool"
done
have node && echo "ready: node $(node --version)"
[[ -f "$HERE/build/csharp-bench/Bench.dll" ]] && echo "ready: csharp baseline"
