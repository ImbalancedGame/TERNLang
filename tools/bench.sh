#!/usr/bin/env bash
# Times every script in bench/ and prints a summary.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
for script in "$HERE"/bench/*.tern; do
  printf '%-16s ' "$(basename "$script")"
  output="$(cd "$HERE" && "$HERE/tools/tern" run "bench/$(basename "$script")" --time 2>&1 | tr '\n' ' ')"
  echo "$output"
done
