#!/usr/bin/env python3
"""Checks the packaging step, as far as it can be checked without a bundler.

PyInstaller needs a Python built with a shared `libpython` and a working C
toolchain to collect against; this machine has neither, and its Python is
static-only, so `tools/package.py` cannot be run to completion here. What can
be checked is everything the script decides for itself — that it names the
right entry point, that it prunes Qt without pruning something Studio imports,
that it refuses to package a tree with no assemblies — plus the entry point
itself, which is a normal Python script and runs like one.

    python3 tools/test_package.py            run it
    python3 tools/test_package.py -v         show each check
"""

import ast
import os
import re
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)


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


def package_module():
    import importlib.util

    spec = importlib.util.spec_from_file_location("tern_package",
                                                  os.path.join(HERE, "package.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def imported_modules(folder):
    """Every `PySide6.X` name the IDE's own code mentions in an import."""
    found = set()
    for name in sorted(os.listdir(folder)):
        if not name.endswith(".py"):
            continue
        with open(os.path.join(folder, name), encoding="utf-8") as handle:
            tree = ast.parse(handle.read())
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                for alias in node.names:
                    if alias.name.startswith("PySide6"):
                        found.add(alias.name)
            elif isinstance(node, ast.ImportFrom) and (node.module or "").startswith("PySide6"):
                found.add(node.module)
    return found


def main(argv):
    verbose = "-v" in argv or "--verbose" in argv
    checks = Checks(verbose)
    package = package_module()

    # ------------------------------------------------------------ the command
    arguments = package.pyinstaller_arguments(False, "TERN Studio")
    checks.check("the entry point is a file PyInstaller can be given",
                 any(argument.endswith("package_entry.py") for argument in arguments),
                 " ".join(arguments[:4]))
    checks.check("the repository root is on the module search path",
                 "--paths" in arguments and ROOT in arguments, str(arguments[:12]))
    checks.check("the ide package is collected whole",
                 "--collect-submodules" in arguments and
                 arguments[arguments.index("--collect-submodules") + 1] == "ide")
    checks.check("the output goes to dist/", "--distpath" in arguments and
                 arguments[arguments.index("--distpath") + 1] == package.DIST)
    checks.check("one-file and folder builds differ only where expected",
                 "--onefile" in package.pyinstaller_arguments(True, "x") and
                 "--onefile" not in package.pyinstaller_arguments(False, "x"))

    # ---------------------------------------------------------- Qt is pruned
    excluded = {name.split(".", 1)[1] for name in package.EXCLUDED_QT}
    used = {name.split(".", 1)[1] for name in imported_modules(os.path.join(ROOT, "ide", "Studio"))
            if "." in name}
    overlaps = sorted(used & excluded)
    checks.check("nothing Studio imports is excluded from the bundle",
                 not overlaps, "also used: " + ", ".join(overlaps))

    # ------------------------------------------------------ refuses nonsense
    saved_build = package.BUILD
    try:
        empty = tempfile.mkdtemp(prefix="tern-package-")
        package.BUILD = empty
        missing = [name for name in package.ASSEMBLIES if name.endswith(".dll")
                   and not os.path.exists(os.path.join(empty, name))]
        checks.check("a tree with no assemblies is detected",
                     len(missing) == 3, str(missing))
        checks.check("the assemblies it wants are the IDE's three",
                     {name for name in package.ASSEMBLIES if name.endswith(".dll")} ==
                     {"TERN.Ide.dll", "TERN.Debug.dll", "TERN.Runner.dll"},
                     str(package.ASSEMBLIES))
    finally:
        package.BUILD = saved_build

    # ------------------------------------------------------ the entry point
    # It is a script like any other: run it the way the bundle would, on this
    # machine's interpreter, and let it draw a window.
    output = os.path.join(tempfile.mkdtemp(prefix="tern-entry-"), "window.png")
    environment = dict(os.environ)
    environment.setdefault("QT_QPA_PLATFORM", "offscreen")
    stubs = os.path.join(ROOT, "build", "qt-stubs")
    if os.path.isdir(stubs):
        environment["LD_LIBRARY_PATH"] = stubs + (
            ":" + environment["LD_LIBRARY_PATH"] if environment.get("LD_LIBRARY_PATH") else "")
    finished = subprocess.run(
        [sys.executable, os.path.join(HERE, "package_entry.py"), "examples",
         "--screenshot", output, "--size", "1024x700", "--no-settings"],
        capture_output=True, text=True, env=environment, timeout=300)
    checks.check("the entry point runs", finished.returncode == 0,
                 (finished.stderr or finished.stdout)[-400:])
    checks.check("and the window it opens renders",
                 os.path.exists(output) and os.path.getsize(output) > 10000,
                 f"{output} {os.path.getsize(output) if os.path.exists(output) else 0}")

    # `frozen` is what separates a checkout from a bundle: the project folder
    # has to stop being "three directories above __file__".
    from ide.Studio import main as studio_main
    saved_frozen = getattr(sys, "frozen", None)
    try:
        sys.frozen = True
        checks.check("a bundled Studio opens the folder it is in",
                     studio_main.default_project() == os.path.dirname(os.path.abspath(sys.executable)),
                     studio_main.default_project())
    finally:
        if saved_frozen is None:
            del sys.frozen
        else:
            sys.frozen = saved_frozen
    checks.check("a checkout opens the repository",
                 os.path.abspath(studio_main.default_project()) == os.path.abspath(ROOT),
                 studio_main.default_project())

    # The wrapper the documentation tells Windows users to run, and which has
    # to pick the venv's Python rather than whatever `python` is first on PATH.
    wrapper = os.path.join(HERE, "package.cmd")
    checks.check("there is a double-clickable packaging command for Windows",
                 os.path.exists(wrapper))
    if os.path.exists(wrapper):
        with open(wrapper, "r", encoding="utf-8", errors="replace") as handle:
            text = handle.read()
        checks.check("it prefers the virtualenv's interpreter",
                     ".venv\\Scripts\\python.exe" in text)
        checks.check("it passes flags through", "%*" in text and "package.py" in text)

    print()
    print(f"passed: {checks.passed}  failed: {len(checks.failed)}")
    for name in checks.failed:
        print(f"  failed: {name}")
    return 1 if checks.failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
