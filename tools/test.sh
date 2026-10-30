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

# So must every page of the MSDN-style reference.
if [[ -d "$HERE/docs/msdn" ]]; then
  if python3 "$HERE/tools/gen-reference.py" --check >/dev/null 2>&1; then
    pass=$((pass+1))
  else
    echo "FAIL docs/msdn is stale (run tools/gen-docs.sh)"
    fail=$((fail+1)); failed_names+=("reference")
  fi
fi

# The IDE: the syntax server speaks the protocol correctly, and the window
# drives that server. Both are skipped cleanly when their prerequisites are
# absent, so a bare checkout can still run the language tests.
if [[ $UPDATE -eq 0 && -f "$HERE/tools/test-ide.sh" ]]; then
  if [[ -f "$HERE/build/TERN.Ide.dll" ]]; then
    if ide_output="$(bash "$HERE/tools/test-ide.sh" 2>&1)"; then
      echo "ok    ide-server  $(tail -1 <<<"$ide_output")"
      pass=$((pass+1))
    else
      echo "FAIL ide-server"
      sed 's/^/      /' <<<"$ide_output" | tail -20
      fail=$((fail+1)); failed_names+=("ide-server")
    fi
  else
    echo "skip  ide-server (no build/TERN.Ide.dll; run tools/build.py --ide)"
  fi
fi

if [[ $UPDATE -eq 0 && -f "$HERE/tools/ide/ui_test.py" ]]; then
  ide_python=""
  for candidate in "$HERE/.venv/bin/python" "/tmp/qtenv/bin/python" python3; do
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c "import PySide6" 2>/dev/null; then
      ide_python="$candidate"; break
    fi
  done

  if [[ -n "$ide_python" && -f "$HERE/build/TERN.Ide.dll" ]]; then
    [[ -d "$HERE/build/qt-stubs" ]] || "$ide_python" "$HERE/tools/ide/qt_stubs.py" >/dev/null 2>&1 || true
    if ui_output="$(cd "$HERE" && QT_QPA_PLATFORM=offscreen \
        LD_LIBRARY_PATH="$HERE/build/qt-stubs${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
        "$ide_python" "$HERE/tools/ide/ui_test.py" 2>&1)"; then
      echo "ok    ide-ui      $(grep -E '^passed:' <<<"$ui_output" | tail -1)"
      pass=$((pass+1))
    else
      echo "FAIL ide-ui"
      grep -vE 'plugin does not|propagateSizeHints|propagateSizeHints|^$' <<<"$ui_output" | tail -25 | sed 's/^/      /'
      fail=$((fail+1)); failed_names+=("ide-ui")
    fi
  else
    echo "skip  ide-ui (needs PySide6)"
  fi
fi

echo
if [[ $UPDATE -eq 1 ]]; then
  echo "expected outputs updated"
  exit 0
fi

echo "passed: $pass  failed: $fail"
[[ $fail -eq 0 ]] || { printf 'failed: %s\n' "${failed_names[*]}"; exit 1; }
