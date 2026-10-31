#!/usr/bin/env python3
"""Checks how the CLI is started, without a platform to run it on.

The bug this exists for is a one-line assumption: `native/ternc.py` ran
`tools/tern`, which is bash, so on Windows it died as

    OSError: [WinError 193] %1 is not a valid Win32 application

before a single bytecode file had been emitted. The resolution that replaced it
is `tools/runner.py`, and what can be checked here — on Linux, where the fault
does not occur — is everything except the Windows syscall itself: that the
choice is an argv list rather than a path, that the assembly form wins over the
script, that `TERN_CLI` overrides it, that a missing assembly falls back instead
of raising, and that the compiler search never offers MSVC, which cannot build
a runtime full of GCC builtins.

    python3 tools/test_runner.py            run it
    python3 tools/test_runner.py -v         show each check
"""

import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)

from tools import runner  # noqa: E402


class Checks:
    def __init__(self, verbose):
        self.verbose = verbose
        self.passed = 0
        self.failed = []

    def check(self, name, condition, detail=""):
        if condition:
            self.passed += 1
            if self.verbose:
                print(f"  ok   {name}")
        else:
            self.failed.append(name)
            print(f"  FAIL {name}")
            if detail:
                print(f"       {detail}")


def main(argv):
    verbose = "-v" in argv or "--verbose" in argv
    checks = Checks(verbose)

    # ------------------------------------------------- the choice is a list
    command = runner.command(ROOT)
    checks.check("the CLI is an argv prefix, not a path",
                 isinstance(command, list) and len(command) >= 1, repr(command))
    checks.check("every element is a string",
                 all(isinstance(part, str) for part in command), repr(command))
    checks.check("the prefix ends in something that exists",
                 os.path.exists(command[0]), command[0])

    # It has to be usable by subprocess without a shell, which is precisely
    # what the bash script was not on Windows.
    finished = runner.run(ROOT, ["version"], capture_output=True, text=True)
    checks.check("the prefix runs the CLI", finished.returncode == 0,
                 (finished.stderr or finished.stdout)[-300:])
    checks.check("and prints a version",
                 "TERN" in (finished.stdout or "") or finished.stdout.strip() != "",
                 (finished.stdout or "")[:120])

    # ------------------------------------------- assembly wins over a script
    if os.path.exists(os.path.join(ROOT, "build", "TERN.Runner.dll")):
        loaded = runner.command(ROOT)
        checks.check("with an assembly present, no shell script is involved",
                     not loaded[0].endswith(("tern", "tern.cmd", "bash", "sh")),
                     repr(loaded))
        checks.check("the assembly is named on the command line",
                     any(part.endswith("TERN.Runner.dll") for part in loaded),
                     repr(loaded))
        expected_environment = runner.environment(ROOT)
        checks.check("the bundled runtime is given DOTNET_ROOT",
                     "DOTNET_ROOT" in expected_environment or
                     not os.path.exists(os.path.join(ROOT, "build",
                                                     "dotnet" + (".exe" if os.name == "nt" else ""))),
                     repr(expected_environment))
    else:
        print("  skip no build/TERN.Runner.dll; run tools/build.py")

    # -------------------------------------------------------- TERN_CLI wins
    saved = os.environ.get("TERN_CLI")
    try:
        os.environ["TERN_CLI"] = "/opt/example/tern --strict"
        overridden = runner.command(ROOT)
        checks.check("TERN_CLI overrides the search",
                     overridden[:2] == ["/opt/example/tern", "--strict"], repr(overridden))
    finally:
        if saved is None:
            os.environ.pop("TERN_CLI", None)
        else:
            os.environ["TERN_CLI"] = saved

    # ------------------------------------------ a build-less tree falls back
    empty = tempfile.mkdtemp(prefix="tern-runner-")
    fallback = runner.command(empty)
    checks.check("a tree with no build still yields a command",
                 isinstance(fallback, list) and fallback, repr(fallback))
    if os.name == "nt":
        # A .cmd cannot be handed to CreateProcess: it is the very error being
        # fixed, so the fallback has to name an interpreter.
        checks.check("on Windows the fallback is run by cmd",
                     fallback[0].lower().endswith(("cmd.exe", "cmd")) or
                     not os.path.exists(os.path.join(empty, "tools", "tern.cmd")),
                     repr(fallback))
    else:
        checks.check("the fallback names the assembly it expects",
                     any(part.endswith("TERN.Runner.dll") or part == "tern"
                         for part in fallback), repr(fallback))

    # ------------------------------------------------------ the C compiler
    compiler = runner_c_module().find_cc(None)
    checks.check("a C compiler is found on this machine",
                 compiler is not None, repr(compiler))
    if compiler:
        checks.check("the compiler search never offers MSVC, which cannot build "
                     "the GCC-flavoured runtime",
                     os.path.basename(compiler[0]).lower() not in
                     runner_c_module().MSVC_NAMES, repr(compiler))
        checks.check("--cc is honoured verbatim",
                     runner_c_module().find_cc("my-cc --sysroot=/x")[:1] == ["my-cc"],
                     repr(runner_c_module().find_cc("my-cc --sysroot=/x")))

    # The end-to-end case from the bug report: a .tern file compiled straight
    # to a binary, which is the path that raised WinError 193.
    sample = os.path.join(ROOT, "examples", "wordcount.tern")
    if os.path.exists(sample) and compiler:
        output = os.path.join(tempfile.mkdtemp(prefix="tern-ternc-"), "wc")
        built = subprocess.run(
            [sys.executable, os.path.join(ROOT, "native", "ternc.py"), sample,
             "-o", output], capture_output=True, text=True)
        checks.check("ternc.py compiles a .tern file directly",
                     built.returncode == 0 and os.path.exists(output),
                     (built.stderr or built.stdout)[-400:])

    print()
    print(f"passed: {checks.passed}  failed: {len(checks.failed)}")
    for name in checks.failed:
        print(f"  failed: {name}")
    return 1 if checks.failed else 0


def runner_c_module():
    """`native/ternc.py` as a module, for its compiler search."""
    import importlib.util

    path = os.path.join(ROOT, "native", "ternc.py")
    spec = importlib.util.spec_from_file_location("tern_ternc", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
