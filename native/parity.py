#!/usr/bin/env python3
"""Differential test: `tern run` versus the compiled binary.

    python3 native/parity.py              # tests/, examples/ and sweep*.tern
    python3 native/parity.py 05 examples  # only names containing a pattern
    python3 native/parity.py --keep       # keep the binaries

`run_tests.py` compares a compiled program against a checked-in golden file.
This script compares it against the interpreter instead, so it also covers
programs that have no golden file (`examples/`) and `sweep*.tern`, which call
every documented method and builtin and prints `repr` or `ERR`. Both are useful
tests for the same reason: any difference is a backend bug.
"""
import argparse
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
if REPO not in sys.path:
    sys.path.insert(0, REPO)
from tools import runner  # noqa: E402

TERN = runner.command(REPO)

# Scripts that exercise the compiler's diagnostics instead of running.
COMPILER_ONLY = {"12_lint", "24_parse_errors"}


def scripts():
    """Every script to compare, in a stable order."""
    found = []
    tests = os.path.join(REPO, "tests")
    for name in sorted(os.listdir(tests)):
        # Compiler-only scripts (lint warnings, syntax errors) never reach a
        # runtime, so there is nothing to compare.
        if name.endswith(".tern") and name[:-5] not in COMPILER_ONLY:
            found.append(os.path.join(tests, name))
    examples = os.path.join(REPO, "examples")
    if os.path.isdir(examples):
        for name in sorted(os.listdir(examples)):
            if name.endswith(".tern"):
                found.append(os.path.join(examples, name))
    # Extra differential sweeps live beside the harness as sweep*.tern.
    for name in sorted(os.listdir(HERE)):
        if name.startswith("sweep") and name.endswith(".tern"):
            found.append(os.path.join(HERE, name))
    return [path for path in found if os.path.exists(path)]


# The script `tools/tern` used to set DOTNET_ROOT itself; running the assembly
# directly means doing it here.
ENVIRONMENT = {**os.environ, **runner.environment(REPO, TERN)}


def run(command, cwd):
    return subprocess.run(command, cwd=cwd, capture_output=True, text=True,
                          encoding="utf-8", errors="surrogateescape", timeout=120,
                          env=ENVIRONMENT)


def main(argv):
    parser = argparse.ArgumentParser(description="Diff the interpreter against compiled code.")
    parser.add_argument("filters", nargs="*", help="substrings of script paths to run")
    parser.add_argument("--keep", metavar="DIR", help="write the binaries to DIR instead of a temp dir")
    args = parser.parse_args(argv)

    chosen = scripts()
    if args.filters:
        chosen = [p for p in chosen if any(f in p for f in args.filters)]
    if not chosen:
        print("no scripts matched")
        return 1

    failed = 0
    build_root = args.keep or tempfile.mkdtemp(prefix="tern-parity-")
    os.makedirs(build_root, exist_ok=True)

    with tempfile.TemporaryDirectory() as scratch:
        for source in chosen:
            label = os.path.relpath(source, REPO)
            binary = os.path.join(build_root, os.path.basename(source)[:-5]
                                  + (".exe" if os.name == "nt" else ""))
            build = subprocess.run(
                [sys.executable, os.path.join(HERE, "ternc.py"), source, "-o", binary],
                capture_output=True, text=True, encoding="utf-8", errors="surrogateescape",
            )
            if build.returncode != 0:
                print("%-28s BUILD FAILED" % label)
                print((build.stderr or build.stdout).strip())
                failed += 1
                continue

            # Both sides run in a scratch directory so a script that writes files
            # cannot disturb the repository, and sees the same paths either way.
            for name in os.listdir(scratch):
                os.remove(os.path.join(scratch, name))
            interpreted = run([*TERN, "run", source], scratch)
            for name in os.listdir(scratch):
                os.remove(os.path.join(scratch, name))
            compiled = run([binary], scratch)

            expected = interpreted.stdout + interpreted.stderr
            actual = compiled.stdout + compiled.stderr
            if expected == actual:
                print("%-28s ok" % label)
                continue

            failed += 1
            print("%-28s DIFFERS" % label)
            # Show the first differing line rather than a whole diff.
            left = expected.splitlines()
            right = actual.splitlines()
            for index in range(max(len(left), len(right))):
                one = left[index] if index < len(left) else "<missing>"
                two = right[index] if index < len(right) else "<missing>"
                if one != two:
                    print("  line %d:" % (index + 1))
                    print("    tern run: %s" % one)
                    print("    native:   %s" % two)
                    break

    total = len(chosen)
    print()
    print("%d compared, %d differing" % (total, failed))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
