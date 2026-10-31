#!/usr/bin/env python3
"""Run the golden tests through the native compiler.

    python3 native/run_tests.py            # every tests/*.tern
    python3 native/run_tests.py 05 14      # only files whose name matches

Each script is exported with `tern emit`, compiled by `native/ternc.py`, run in
the `tests/` directory and diffed against the matching `.out` file. Scripts that
the native backend cannot compile yet are reported as SKIP with the reason, so
the table doubles as a to-do list.

Every script is run twice: once normally and once with `TERN_GC_THRESHOLD=4096`,
which makes the collector run constantly and turns any missed root into a
wrong answer or a crash. `--no-stress` runs only the normal pass.
"""
import argparse
import difflib
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
TESTS = os.path.join(REPO, "tests")

# Scripts that never reach a runtime: they exercise the compiler's diagnostics.
COMPILER_ONLY = {"12_lint", "24_parse_errors"}


def main(argv):
    parser = argparse.ArgumentParser(description="Run golden tests natively.")
    parser.add_argument("filters", nargs="*", help="substrings of test names to run")
    parser.add_argument("--timeout", type=float, default=60.0, help="per-test seconds")
    parser.add_argument("--verbose", action="store_true", help="print full diffs")
    parser.add_argument("--no-stress", action="store_true",
                        help="skip the aggressive-collection pass")
    args = parser.parse_args(argv)

    names = sorted(
        name[:-5]
        for name in os.listdir(TESTS)
        if name.endswith(".tern") and os.path.exists(os.path.join(TESTS, name[:-5] + ".out"))
        # These two check the compiler, not the runtime: 12_lint covers the
        # lint warnings and 24_parse_errors the diagnostics for bad syntax,
        # neither of which a compiled binary produces.
        and name[:-5] not in COMPILER_ONLY
    )
    if args.filters:
        names = [n for n in names if any(f in n for f in args.filters)]

    passed, failed, skipped = 0, [], []
    with tempfile.TemporaryDirectory() as build:
        for name in names:
            source = os.path.join(TESTS, name + ".tern")
            # surrogateescape: a golden file may legitimately hold bytes that are
            # not valid UTF-8 (a lone surrogate printed by the interpreter), and
            # both sides must compare byte for byte.
            expected = open(os.path.join(TESTS, name + ".out"), encoding="utf-8",
                            errors="surrogateescape").read()
            # ternc.py appends .exe on Windows, so the path that gets executed
            # has to be the one it wrote, not the one that was asked for.
            binary = os.path.join(build, name + (".exe" if os.name == "nt" else ""))

            build_result = subprocess.run(
                [sys.executable, os.path.join(HERE, "ternc.py"), source, "-o", binary],
                capture_output=True, text=True,
            )
            if build_result.returncode != 0:
                reason = (build_result.stderr or build_result.stdout).strip().splitlines()
                skipped.append((name, reason[-1] if reason else "build failed"))
                continue

            try:
                run = subprocess.run(
                    [binary], cwd=TESTS, capture_output=True, text=True, timeout=args.timeout,
                    encoding="utf-8", errors="surrogateescape",
                )
            except subprocess.TimeoutExpired:
                failed.append((name, "timeout after %.0fs" % args.timeout))
                continue

            # Like tools/test.sh: compare the text, not the exit status. Some
            # scripts end with an uncaught error on purpose.
            actual = run.stdout + run.stderr
            problem = None if actual == expected else "output differs"

            # Second pass with a tiny GC threshold: the collector then runs
            # thousands of times per script, so a missing root shows up here.
            if problem is None and not args.no_stress:
                try:
                    stress = subprocess.run(
                        [binary], cwd=TESTS, capture_output=True, text=True,
                        timeout=args.timeout, encoding="utf-8", errors="surrogateescape",
                        env={**os.environ, "TERN_GC_THRESHOLD": "4096"},
                    )
                except subprocess.TimeoutExpired:
                    problem = "timeout under GC stress"
                else:
                    if stress.stdout + stress.stderr != expected:
                        problem = "differs under GC stress (missed root?)"

            if problem is None:
                passed += 1
                continue
            failed.append((name, problem))

            if args.verbose:
                # difflib rather than the `diff` command: Windows has neither.
                print("".join(difflib.unified_diff(
                    expected.splitlines(keepends=True), actual.splitlines(keepends=True),
                    fromfile=name + ".out", tofile="actual")), end="")

    width = max([len(n) for n in names] + [4])
    for name in names:
        entry = next((f for f in failed if f[0] == name), None)
        skip = next((s for s in skipped if s[0] == name), None)
        if entry:
            print("%-*s FAIL  %s" % (width, name, entry[1]))
        elif skip:
            print("%-*s SKIP  %s" % (width, name, skip[1]))
        else:
            print("%-*s ok" % (width, name))

    print("\n%d passed, %d failed, %d skipped" % (passed, len(failed), len(skipped)))
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
