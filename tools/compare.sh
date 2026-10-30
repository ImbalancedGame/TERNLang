#!/usr/bin/env bash
# Runs the comparative benchmark suite (TERN vs Lua 5.4, LuaJIT, Node.js, C#)
# and prints a table of in-process timings (best of N runs, milliseconds).
#
#   tools/compare.sh [--runs N] [--markdown] [--only lang,lang]
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CACHE="$HERE/build/langs"
SRC="$HERE/bench/compare"

RUNS=5
MARKDOWN=0
ONLY=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --runs) RUNS="${2:-5}"; shift 2 ;;
    --markdown) MARKDOWN=1; shift ;;
    --only) ONLY="${2:-}"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

wanted() { [[ -z "$ONLY" ]] || [[ ",$ONLY," == *",$1,"* ]]; }

declare -a LANGS=()
declare -a CMDS=()

add_lang() {
  local name="$1"; shift
  wanted "$name" || return 0
  LANGS+=("$name")
  CMDS+=("$*")
}

add_lang tern   "$HERE/tools/tern run $SRC/bench.tern"

# The compiled backend, from the same source: same workloads, same in-process
# clock, so the table shows what compiling buys. Rebuilt when the source or the
# compiler is newer than the binary.
TERN_NATIVE="$HERE/build/langs/bench_ternc"
if [[ ! -x "$TERN_NATIVE" ]] || [[ "$SRC/bench.tern" -nt "$TERN_NATIVE" ]] || [[ "$HERE/native/ternc.py" -nt "$TERN_NATIVE" ]]; then
  if ! python3 "$HERE/native/ternc.py" "$SRC/bench.tern" -o "$TERN_NATIVE" >/dev/null 2>&1; then
    echo "warning: the compiled backend did not build, ternc column skipped" >&2
    rm -f "$TERN_NATIVE"
  fi
fi
[[ -x "$TERN_NATIVE" ]] && add_lang ternc "$TERN_NATIVE"
add_lang lua    "$CACHE/lua/bin/lua $SRC/bench.lua"
add_lang luajit "$CACHE/luajit/bin/luajit $SRC/bench.lua"
add_lang node   "node $SRC/bench.js"
add_lang csharp "env DOTNET_ROOT=${TERN_TOOLCHAIN_DIR:-/tmp/tern-toolchain}/dotnet $HERE/build/csharp-bench/dotnet $HERE/build/csharp-bench/Bench.dll"

WORKLOADS=(fib loop maps bintree strings)
declare -A BEST
declare -A RESULT

for i in "${!LANGS[@]}"; do
  lang="${LANGS[$i]}"
  cmd="${CMDS[$i]}"

  for run in $(seq 1 "$RUNS"); do
    out="$($cmd 2>/dev/null)"
    if [[ -z "$out" ]]; then
      echo "warning: $lang produced no output (skipped)" >&2
      continue
    fi

    while read -r name result ms; do
      [[ -z "$name" ]] && continue
      key="$lang:$name"
      current="${BEST[$key]:-}"
      if [[ -z "$current" ]] || (( $(printf '%.0f' "$ms") < $(printf '%.0f' "$current") )); then
        BEST[$key]="$ms"
        RESULT[$key]="$result"
      fi
    done <<< "$out"
  done
done

# ---------------------------------------------------------------- report
if [[ $MARKDOWN -eq 1 ]]; then
  printf '| Workload |'
  for lang in "${LANGS[@]}"; do printf ' %s |' "$lang"; done
  printf ' TERN vs lua |\n'
  printf '| --- |'
  for lang in "${LANGS[@]}"; do printf ' ---: |'; done
  printf ' ---: |\n'
  for w in "${WORKLOADS[@]}"; do
    tern="${BEST[tern:$w]:-}"
    luaMs="${BEST[lua:$w]:-}"
    printf '| `%s` |' "$w"
    for lang in "${LANGS[@]}"; do
      v="${BEST[$lang:$w]:-n/a}"
      if [[ "$v" != "n/a" ]]; then printf ' %s ms |' "$(printf '%.0f' "$v")"; else printf ' n/a |'; fi
    done
    if [[ -n "$tern" && -n "$luaMs" ]] && (( $(printf '%.0f' "$luaMs") > 0 )); then
      ratio=$(awk -v a="$tern" -v b="$luaMs" 'BEGIN { printf "%.2f", a / b }')
      printf ' %sx |\n' "$ratio"
    else
      printf ' n/a |\n'
    fi
  done
  exit 0
fi

echo "TERN comparative benchmark (best of $RUNS runs, in-process ms)"
echo

# header
printf '%-9s' "workload"
for lang in "${LANGS[@]}"; do printf '%12s' "$lang"; done
printf '%12s\n' "tern/lua"

for w in "${WORKLOADS[@]}"; do
  printf '%-9s' "$w"
  for lang in "${LANGS[@]}"; do
    v="${BEST[$lang:$w]:-}"
    if [[ -n "$v" ]]; then printf '%12s' "$(printf '%.0f' "$v")"; else printf '%12s' "-"; fi
  done

  tern="${BEST[tern:$w]:-}"
  luaMs="${BEST[lua:$w]:-}"
  if [[ -n "$tern" && -n "$luaMs" ]]; then
    printf '%11sx\n' "$(awk -v a="$tern" -v b="$luaMs" 'BEGIN { printf "%.2f", a / b }')"
  else
    printf '%12s\n' "-"
  fi
done

echo
echo "results (checked for agreement across languages):"
for w in "${WORKLOADS[@]}"; do
  line=""
  for lang in "${LANGS[@]}"; do
    r="${RESULT[$lang:$w]:-}"
    [[ -n "$r" ]] && line+=" $lang=$r"
  done
  echo "  $w:$line"
done
