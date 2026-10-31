#!/usr/bin/env python3
"""Compile and time the benchmark suite with the native backend.

    python3 native/bench.py            # default: 7 runs per workload, best kept
    python3 native/bench.py --runs 9
    python3 native/bench.py --interp   # also time `tern run` for a comparison

Every workload is compiled once (bytecode export -> C -> ELF) and then run
`--runs` times; the fastest wall-clock run is reported.
"""
import argparse
import os
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
BENCH = os.path.join(REPO, "bench")
TERNC = os.path.join(HERE, "ternc.py")
if REPO not in sys.path:
    sys.path.insert(0, REPO)
from tools import runner  # noqa: E402

TERN = runner.command(REPO)
# `tools/tern` set this for itself; a directly started assembly needs it.
ENVIRONMENT = {**os.environ, **runner.environment(REPO, TERN)}

WORKLOADS = ["fib", "loop", "maps", "strings", "bintree"]


def best_run(command, runs, cwd=None):
    best = float("inf")
    for _ in range(runs):
        start = time.perf_counter()
        done = subprocess.run(command, cwd=cwd, stdout=subprocess.DEVNULL,
                              stderr=subprocess.DEVNULL, env=ENVIRONMENT)
        if done.returncode != 0:
            # Timing a workload that crashed would report a meaningless number.
            return None
        best = min(best, time.perf_counter() - start)
    return best * 1000.0


def main(argv):
    parser = argparse.ArgumentParser(description="Time the benchmarks natively.")
    parser.add_argument("workloads", nargs="*", default=None, help="subset of %s" % ", ".join(WORKLOADS))
    parser.add_argument("--runs", type=int, default=7)
    parser.add_argument("--interp", action="store_true", help="also time the interpreter")
    parser.add_argument("--cflags", default="-O2", help="flags for the C compiler")
    args = parser.parse_args(argv)

    names = args.workloads or WORKLOADS
    rows = []

    with tempfile.TemporaryDirectory() as build:
        for name in names:
            source = os.path.join(BENCH, name + ".tern")
            binary = os.path.join(build, name + (".exe" if os.name == "nt" else ""))
            built = subprocess.run(
                [sys.executable, TERNC, source, "-o", binary, "--cflags=" + args.cflags],
                capture_output=True, text=True,
            )
            if built.returncode != 0:
                print("%s: build failed\n%s" % (name, built.stderr or built.stdout))
                continue

            native = best_run([binary], args.runs)
            if native is None:
                print("%s: exited with an error; fix the workload before timing it" % name)
                continue

            row = {"name": name, "native": native}
            if args.interp:
                row["interp"] = best_run([*TERN, "run", source], args.runs, cwd=REPO)
            rows.append(row)

    if args.interp:
        print("%-10s %10s %10s %8s" % ("workload", "native ms", "tern ms", "speedup"))
        for row in rows:
            print("%-10s %10.1f %10.1f %7.2fx"
                  % (row["name"], row["native"], row["interp"], row["interp"] / row["native"]))
    else:
        print("%-10s %10s" % ("workload", "native ms"))
        for row in rows:
            print("%-10s %10.1f" % (row["name"], row["native"]))

    print("\nbest of %d runs; native times include process start-up" % args.runs)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
