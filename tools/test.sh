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
  if diff -q <(cd "$HERE" && timeout 120 "$TERN" docs) "$HERE/docs/stdlib.md" >/dev/null 2>&1; then
    pass=$((pass+1))
  else
    echo "FAIL docs/stdlib.md is stale (run tools/gen-docs.sh)"
    fail=$((fail+1)); failed_names+=("docs")
  fi
fi

# So must every page of the MSDN-style reference.
if [[ -d "$HERE/docs/msdn" ]]; then
  if timeout 300 python3 "$HERE/tools/gen-reference.py" --check >/dev/null 2>&1; then
    pass=$((pass+1))
  else
    echo "FAIL docs/msdn is stale (run tools/gen-docs.sh)"
    fail=$((fail+1)); failed_names+=("reference")
  fi
fi

# How the command line is started, which is where a Windows first run died:
# `tools/tern` is bash, and CreateProcess cannot execute it.
if [[ $UPDATE -eq 0 && -f "$HERE/tools/test_runner.py" ]]; then
  if runner_output="$(cd "$HERE" && timeout 300 python3 "$HERE/tools/test_runner.py" 2>&1)"; then
    echo "ok    runner      $(tail -1 <<<"$runner_output")"
    pass=$((pass+1))
  else
    echo "FAIL runner"
    tail -20 <<<"$runner_output" | sed 's/^/      /'
    fail=$((fail+1)); failed_names+=("runner")
  fi
fi

# The source itself: a docstring with an escape Python does not know is a wall of
# SyntaxWarnings on 3.13, and a Windows path written with a *known* escape is a
# tab character that no test would ever notice.
if [[ $UPDATE -eq 0 && -f "$HERE/tools/test_sources.py" ]]; then
  if source_output="$(cd "$HERE" && timeout 120 python3 "$HERE/tools/test_sources.py" 2>&1)"; then
    echo "ok    sources     $(tail -1 <<<"$source_output")"
    pass=$((pass+1))
  else
    echo "FAIL sources"
    tail -20 <<<"$source_output" | sed 's/^/      /'
    fail=$((fail+1)); failed_names+=("sources")
  fi
fi

# The standard library has to be the same on both back ends and on every
# platform. This is static: the Windows half of the runtime cannot be executed
# here, and a module left inside the non-Windows branch is exactly the kind of
# bug no Linux run can see (it cost a user a "map has no method 'available'").
if [[ $UPDATE -eq 0 && -f "$HERE/tools/test_namespaces.py" && -f "$HERE/build/TERN.Runner.dll" ]]; then
  if namespace_output="$(cd "$HERE" && timeout 300 python3 "$HERE/tools/test_namespaces.py" 2>&1)"; then
    echo "ok    native-ns   $(tail -1 <<<"$namespace_output")"
    pass=$((pass+1))
  else
    echo "FAIL native-ns"
    tail -20 <<<"$namespace_output" | sed 's/^/      /'
    fail=$((fail+1)); failed_names+=("native-ns")
  fi
elif [[ $UPDATE -eq 0 ]]; then
  echo "skip  native-ns (needs build/TERN.Runner.dll)"
fi

# Everything that needs PySide6 uses the same interpreter, found once.
IDE_PYTHON=""
if [[ $UPDATE -eq 0 ]]; then
  for candidate in "$HERE/.venv/bin/python" "/tmp/qtenv/bin/python" python3; do
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c "import PySide6" 2>/dev/null; then
      IDE_PYTHON="$candidate"; break
    fi
  done
fi

# Qt links libGL, libEGL, libxkbcommon and libdbus-1 even when only the offscreen
# platform is used, and a machine without a display has none of them. The
# stand-ins are built once here rather than inside the window test, because the
# packaging check below imports QtWidgets too and runs first: on a clean build
# it used to fail with "libGL.so.1: cannot open shared object file" while the
# window test further down passed, having created them on the way.
if [[ -n "$IDE_PYTHON" && ! -d "$HERE/build/qt-stubs" ]]; then
  "$IDE_PYTHON" "$HERE/tools/ide/qt_stubs.py" >/dev/null 2>&1 || true
fi

# Packaging: the entry point, the PyInstaller command, and what it prunes.
if [[ $UPDATE -eq 0 && -n "$IDE_PYTHON" && -f "$HERE/tools/test_package.py" ]]; then
  if package_output="$(cd "$HERE" && timeout 400 env QT_QPA_PLATFORM=offscreen \
      LD_LIBRARY_PATH="$HERE/build/qt-stubs${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
      "$IDE_PYTHON" "$HERE/tools/test_package.py" 2>&1)"; then
    echo "ok    package     $(tail -1 <<<"$package_output")"
    pass=$((pass+1))
  else
    echo "FAIL package"
    tail -20 <<<"$package_output" | sed 's/^/      /'
    fail=$((fail+1)); failed_names+=("package")
  fi
fi

# The IDE: the syntax server speaks the protocol correctly, and the window
# drives that server. Both are skipped cleanly when their prerequisites are
# absent, so a bare checkout can still run the language tests.
if [[ $UPDATE -eq 0 && -f "$HERE/tools/test-ide.sh" ]]; then
  if [[ -f "$HERE/build/TERN.Ide.dll" ]]; then
    # Every IDE suite gets a hard limit: a hung child holding the pipe open
    # would otherwise stall the whole run with no output at all.
    if ide_output="$(timeout 300 bash "$HERE/tools/test-ide.sh" 2>&1)"; then
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

if [[ $UPDATE -eq 0 && -f "$HERE/tools/debug_test.py" && -f "$HERE/build/TERN.Debug.dll" ]]; then
  if debug_output="$(timeout 300 "$(bash "$HERE/tools/tern-python")" "$HERE/tools/debug_test.py" 2>&1)"; then
    echo "ok    debugger    $(tail -1 <<<"$debug_output")"
    pass=$((pass+1))
  else
    echo "FAIL debugger"
    tail -20 <<<"$debug_output" | sed 's/^/      /'
    fail=$((fail+1)); failed_names+=("debugger")
  fi
else
  [[ $UPDATE -eq 0 ]] && echo "skip  debugger (needs build/TERN.Debug.dll and a .NET runtime)"
fi

if [[ $UPDATE -eq 0 && -f "$HERE/tools/ide/ui_test.py" ]]; then
  ide_python="$IDE_PYTHON"
  if [[ -n "$ide_python" && -f "$HERE/build/TERN.Ide.dll" ]]; then
    if ui_output="$(cd "$HERE" && timeout 900 env QT_QPA_PLATFORM=offscreen \
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
