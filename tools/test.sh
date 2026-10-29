#!/usr/bin/env bash
# Runs the TERN test suite. Each tests/NN_name.tern is compared against
# tests/NN_name.out (stdout+stderr). Use --update to rewrite the expected files.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TERN="$HERE/tools/tern"
UPDATE=0
[[ "${1:-}" == "--update" ]] && UPDATE=1

pass=0; fail=0; failed_names=()

for script in "$HERE"/tests/*.tern; do
  name="$(basename "$script" .tern)"
  case "$name" in
    *_lib) continue ;;
  esac

  expected="$HERE/tests/$name.out"
  actual="$(cd "$HERE" && timeout 30 "$TERN" run "tests/$name.tern" 2>&1)"

  if [[ $UPDATE -eq 1 ]]; then
    printf '%s\n' "$actual" > "$expected"
    echo "updated $name"
    continue
  fi

  if [[ ! -f "$expected" ]]; then
    echo "MISSING expected output for $name"
    fail=$((fail+1)); failed_names+=("$name")
    continue
  fi

  if [[ "$actual" == "$(cat "$expected")" ]]; then
    pass=$((pass+1))
  else
    echo "FAIL $name"
    diff -u "$expected" <(printf '%s\n' "$actual") | head -20
    fail=$((fail+1)); failed_names+=("$name")
  fi
done

# The generated standard library reference must match the code.
if [[ -f "$HERE/docs/stdlib.md" ]]; then
  if diff -q <(cd "$HERE" && "$TERN" docs) "$HERE/docs/stdlib.md" >/dev/null 2>&1; then
    pass=$((pass+1))
  else
    echo "FAIL docs/stdlib.md is stale (run tools/gen-docs.sh)"
    fail=$((fail+1)); failed_names+=("docs")
  fi
fi

echo
if [[ $UPDATE -eq 1 ]]; then
  echo "expected outputs updated"
  exit 0
fi

echo "passed: $pass  failed: $fail"
[[ $fail -eq 0 ]] || { printf 'failed: %s\n' "${failed_names[*]}"; exit 1; }
