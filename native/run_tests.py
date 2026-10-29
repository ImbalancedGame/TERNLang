#!/usr/bin/env python3
"""Run the golden tests through the native compiler.

    python3 native/run_tests.py            # every tests/*.tern
    python3 native/run_tests.py 05 14      # only files whose name matches

Each script is exported with `tern emit`, compiled by `native/ternc.py`, run in
the `tests/` directory and diffed against the matching `.out` file. Scripts that
the native backend cannot compile yet are reported as SKIP with the reason, so
the table doubles as a to-do list.
"""
import argparse
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
TESTS = os.path.join(REPO, "tests")
TERN = os.path.join(REPO, "tools", "tern")


def main(argv):
    parser = argparse.ArgumentParser(description="Run golden tests natively.")
    parser.add_argument("filters", nargs="*", help="substrings of test names to run")
    parser.add_argument("--timeout", type=float, default=60.0, help="per-test seconds")
    parser.add_argument("--verbose", action="store_true", help="print full diffs")
    args = parser.parse_args(argv)

    names = sorted(
        name[:-5]
        for name in os.listdir(TESTS)
        if name.endswith(".tern") and os.path.exists(os.path.join(TESTS, name[:-5] + ".out"))
        # 12_lint checks compiler diagnostics, which the native backend does not emit.
        and not name.endswith("12_lint.tern")
    )
    if args.filters:
        names = [n for n in names if any(f in n for f in args.filters)]

    passed, failed, skipped = 0, [], []
    with tempfile.TemporaryDirectory() as build:
        for name in names:
            source = os.path.join(TESTS, name + ".tern")
            expected = open(os.path.join(TESTS, name + ".out"), encoding="utf-8").read()
            binary = os.path.join(build, name)

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
                )
            except subprocess.TimeoutExpired:
                failed.append((name, "timeout after %.0fs" % args.timeout))
                continue

            # Like tools/test.sh: compare the text, not the exit status. Some
            # scripts end with an uncaught error on purpose.
            actual = run.stdout + run.stderr
            if actual != expected:
                failed.append((name, "output differs"))
            else:
                passed += 1
                continue

            if args.verbose:
                diff = subprocess.run(["diff", "-u", os.path.join(TESTS, name + ".out"), "-"],
                                      input=actual, capture_output=True, text=True)
                print(diff.stdout)

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
