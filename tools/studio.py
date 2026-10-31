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


def find_python():
    """An interpreter with PySide6, or None.

    The launcher is itself running on one, so it is tried first; then the usual
    places a virtualenv can be, which differ between platforms.
    """
    candidates = [sys.executable]
    for name in (".venv", "venv"):
        for folder in ("bin", "Scripts"):
            candidate = ROOT / name / folder / ("python.exe" if WINDOWS else "python")
            if candidate.exists():
                candidates.append(str(candidate))
    candidates.extend(["/tmp/qtenv/bin/python", shutil.which("python3") or "", shutil.which("python") or ""])

    for candidate in candidates:
        if not candidate or not os.path.exists(candidate):
            continue
        try:
            result = subprocess.run([candidate, "-c", "import PySide6"], capture_output=True)
        except OSError:
            continue
        if result.returncode == 0:
            return candidate
    return None


def install_hint():
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

    print("cannot build: no toolchain and no `dotnet` SDK on PATH.\n"
          "Install the .NET 8 SDK, or run tools/setup-toolchain.sh where a shell\n"
          "and a C compiler are available.", file=sys.stderr)
    return False


def prepare_environment(argv):
    """Qt on a headless machine, and DOTNET_ROOT when the runtime is bundled."""
    if "--screenshot" in argv or "--offscreen" in argv:
        os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
        stubs = ROOT / "build" / "qt-stubs"
        if not stubs.exists() and not WINDOWS:
            subprocess.run([sys.executable, str(ROOT / "tools" / "ide" / "qt_stubs.py")],
                           capture_output=True)
        if stubs.exists():
            existing = os.environ.get("LD_LIBRARY_PATH", "")
            os.environ["LD_LIBRARY_PATH"] = str(stubs) + (os.pathsep + existing if existing else "")

    sys.path.insert(0, str(ROOT))
    from ide.Studio.runtime import environment

    for name, value in environment(str(ROOT)).items():
        os.environ.setdefault(name, value)


def main(argv):
    os.chdir(ROOT)

    python = find_python()
    if python is None:
        print("TERN Studio needs PySide6:\n\n" + install_hint(), file=sys.stderr)
        return 1

    if not build_if_needed():
        return 1

    prepare_environment(argv)

    # Re-enter the app under the interpreter that has PySide6, keeping this
    # process as a thin parent so Ctrl+C and exit codes behave.
    command = [python, "-m", "ide.Studio", *argv]
    if WINDOWS:
        return subprocess.run(command, cwd=str(ROOT)).returncode
    os.execv(python, command)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
