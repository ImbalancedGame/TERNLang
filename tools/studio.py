#!/usr/bin/env python3
"""Launch TERN Studio, on any platform.

    python tools/ide.py                    open this repository
    python tools/ide.py examples           open a directory
    python tools/ide.py --screenshot x.png render once and exit (headless)

Windows: `tools\\ide.cmd` does the same thing. Unix: `tools/ide.sh`.

Why a Python launcher as well as the shell script: the shell script only works
where bash and `pip`-style virtualenv paths exist, and Windows has neither, so
the logic lives here, once, and the two shells are thin wrappers.
"""

import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WINDOWS = os.name == "nt"

sys.path.insert(0, str(ROOT))


def search_roots(invoked_from=None):
    r"""Directories a virtualenv might be in, nearest first.

    The repository root, then the directory the launcher was invoked from and
    its parents. The second part is not decoration: `cd tools` then
    `python -m venv .venv` puts the environment in `tools\.venv`, and a
    launcher that only looked at the root would tell someone their perfectly
    good install did not exist.
    """
    # The invocation directory and its parents first, nearest first, then the
    # repository root: somebody who made the virtualenv where they were standing
    # meant that one.
    order = []
    if invoked_from is not None:
        try:
            start = invoked_from.resolve()
            order.extend([start, *list(start.parents)[:4]])
        except OSError:
            pass
    order.append(ROOT)

    roots = []
    for directory in order:
        if directory not in roots:
            roots.append(directory)
    return roots


def venv_pythons(invoked_from=None):
    """Every interpreter a virtualenv in the search roots would have."""
    found = []
    for root in search_roots(invoked_from):
        for name in (".venv", "venv"):
            for folder in ("Scripts", "bin"):
                for executable in ("python.exe", "python"):
                    candidate = root / name / folder / executable
                    if candidate.exists() and str(candidate) not in found:
                        found.append(str(candidate))
    return found


def find_python(invoked_from=None):
    """An interpreter with PySide6, or None.

    The launcher is itself running on one, so it is tried first; then every
    virtualenv in the search roots, which is where a deliberate install lives;
    then whatever `python3` and `python` resolve to. Each candidate is asked to
    import PySide6, because the name of the directory is not proof of anything.
    """
    candidates = [sys.executable, *venv_pythons(invoked_from)]
    candidates.extend(["/tmp/qtenv/bin/python", shutil.which("python3") or "",
                       shutil.which("python") or ""])

    seen = set()
    for candidate in candidates:
        if not candidate or candidate in seen or not os.path.exists(candidate):
            continue
        seen.add(candidate)
        try:
            result = subprocess.run([candidate, "-c", "import PySide6"], capture_output=True)
        except OSError:
            continue
        if result.returncode == 0:
            return candidate
    return None


def install_hint():
    """The two commands to run, for this platform, from this directory."""
    if WINDOWS:
        return ("    python -m venv .venv\n"
                "    .venv\\Scripts\\pip install \"PySide6-Essentials==6.9.3\"\n")
    return ("    python3 -m venv .venv\n"
            "    .venv/bin/pip install \"PySide6-Essentials==6.9.3\"\n")


def build_if_needed():
    """Builds the runner, syntax server and debugger when any is missing.

    Run and Debug are part of the IDE, so all three go together: a launch that
    built only the syntax server would fail later with a message about a file
    the user never asked for.

    Two builders. The sandbox toolchain (Roslyn from an npm package) when it is
    present, because that is what the Linux container uses; otherwise the .NET
    SDK's own `dotnet build`, which is what a normal machine has.
    """
    sys.path.insert(0, str(ROOT))
    from ide.Studio.runtime import assembly, toolchain_directory

    missing = [name for name in ("TERN.Runner", "TERN.Ide", "TERN.Debug")
               if assembly(str(ROOT), name) is None]
    if not missing:
        return True

    print(f"building {', '.join(missing)}...", file=sys.stderr)

    if (Path(toolchain_directory()) / ".ready").exists() and not WINDOWS:
        result = subprocess.run([sys.executable, str(ROOT / "tools" / "build.py"), "--all"])
        return result.returncode == 0

    sdk = shutil.which("dotnet")
    if sdk:
        projects = {
            "TERN.Runner": ROOT / "TernRunner" / "TernRunner.csproj",
            "TERN.Ide": ROOT / "ide" / "Server" / "TERN.Ide.csproj",
            "TERN.Debug": ROOT / "ide" / "Debug" / "TERN.Debug.csproj",
        }
        for name in missing:
            project = projects[name]
            if not project.exists():
                print(f"cannot build {name}: {project} is missing", file=sys.stderr)
                return False
            result = subprocess.run(
                [sdk, "build", str(project), "-c", "Release", "--nologo", "-v", "quiet"],
                cwd=str(ROOT))
            if result.returncode != 0:
                return False
        return True

    if WINDOWS:
        print("cannot build: `dotnet` was not found on PATH.\n\n"
              "Studio needs the .NET 8 SDK to build the syntax server and the\n"
              "debugger:\n\n"
              "    winget install Microsoft.DotNet.SDK.8\n\n"
              "then close and reopen the terminal so PATH is picked up, and run\n"
              "this launcher again.", file=sys.stderr)
    else:
        print("cannot build: no toolchain and no `dotnet` SDK on PATH.\n\n"
              "Either install the .NET 8 SDK, or bootstrap the sandbox toolchain:\n\n"
              "    bash tools/setup-toolchain.sh\n", file=sys.stderr)
    return False


def prepare_environment(argv, python=None):
    """Qt on a headless machine, and DOTNET_ROOT when the runtime is bundled."""
    if "--screenshot" in argv or "--offscreen" in argv:
        os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
        if not WINDOWS:
            stubs = ROOT / "build" / "qt-stubs"
            # The generator asks PySide6 which libraries Qt links, so it has to
            # run under the interpreter that has PySide6 — not whichever one
            # started this launcher. An existing but empty directory does not
            # count as built.
            if not any(stubs.glob("libGL*")):
                subprocess.run([python or sys.executable,
                                str(ROOT / "tools" / "ide" / "qt_stubs.py")],
                               capture_output=True)
            if any(stubs.glob("libGL*")):
                existing = os.environ.get("LD_LIBRARY_PATH", "")
                os.environ["LD_LIBRARY_PATH"] = (str(stubs)
                                                 + (os.pathsep + existing if existing else ""))
            else:
                print("warning: could not build the Qt stand-ins for a headless run;\n"
                      "         if Qt fails to load libGL, run this with a display instead.",
                      file=sys.stderr)

    sys.path.insert(0, str(ROOT))
    from ide.Studio.runtime import environment

    for name, value in environment(str(ROOT)).items():
        os.environ.setdefault(name, value)


def main(argv):
    # Where the launcher was called from, before changing directory: a
    # virtualenv made there is as real as one at the root.
    invoked_from = Path.cwd()
    os.chdir(ROOT)

    python = find_python(invoked_from)
    if python is None:
        searched = [str(root / name)
                    for root in search_roots(invoked_from)
                    for name in (".venv", "venv")]
        print("TERN Studio needs PySide6.\n\n"
              + install_hint()
              + "\nLooked for a virtualenv in:\n"
              + "".join(f"    {path}\n" for path in searched[:8])
              + f"\nand for PySide6 in {sys.executable} and on PATH.",
              file=sys.stderr)
        return 1

    if not build_if_needed():
        return 1

    prepare_environment(argv, python)

    # Re-enter the app under the interpreter that has PySide6, keeping this
    # process as a thin parent so Ctrl+C and exit codes behave.
    command = [python, "-m", "ide.Studio", *argv]
    if WINDOWS:
        return subprocess.run(command, cwd=str(ROOT)).returncode
    os.execv(python, command)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
