#!/usr/bin/env python3
"""Packages TERN Studio into an application you can double-click.

    python tools/package.py                 # dist/TERN Studio/ (recommended)
    python tools/package.py --onefile       # one executable, slower to start
    python tools/package.py --clean         # start from scratch

PyInstaller is a build-time dependency only: it is not imported by Studio and
nothing it installs is needed to run a checkout. On Windows the result is
`dist\\TERN Studio\\TERN Studio.exe`, with an icon-less Start Menu shortcut you
can make yourself; on Linux and macOS the same directory holds a binary with
the same name.

## What ends up in the folder

* Python, the `ide` package and the PySide6 modules Studio imports. Qt is pruned
  to the modules actually used — no WebEngine, no Qt3D, no designer plugins.
* `build\\*.dll` — the syntax server, the debugger and the CLI, copied next to
  the executable so `runtime.assembly()` finds them the way it does in a
  checkout. They are not embedded: the .NET runtime is not ours to ship, and
  the assemblies are only useful next to one.

## What it still needs

The .NET 8 runtime. A machine without it starts the window and the editor, and
every language feature then says so instead of failing silently — the runner
line in the Run window names the command and `runtime.missing` explains what is
absent. Bundling the runtime would mean publishing the three projects
self-contained (~70 MB each) and is a separate decision, not a packaging flag.

A packaged Studio finds its project the same way a checkout does: the directory
argument, then the remembered one, then the folder above the executable.
"""

import argparse
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILD = os.path.join(ROOT, "build")
DIST = os.path.join(ROOT, "dist")
ENTRY = os.path.join(ROOT, "tools", "package_entry.py")
ASSEMBLIES = ("TERN.Ide.dll", "TERN.Debug.dll", "TERN.Runner.dll",
              "TERN.Runner.runtimeconfig.json", "TERN.Ide.runtimeconfig.json",
              "TERN.Debug.runtimeconfig.json")

# Qt modules Studio imports. PyInstaller's PySide6 hook collects the whole of
# Qt by default — several hundred megabytes of WebEngine, Multimedia and
# Quick3D that nothing here touches.
EXCLUDED_QT = [
    "PySide6.QtWebEngineCore", "PySide6.QtWebEngineWidgets", "PySide6.QtWebEngineQuick",
    "PySide6.QtMultimedia", "PySide6.QtMultimediaWidgets", "PySide6.QtQuick",
    "PySide6.QtQuick3D", "PySide6.QtQml", "PySide6.Qt3DCore", "PySide6.Qt3DRender",
    "PySide6.QtCharts", "PySide6.QtDataVisualization", "PySide6.QtBluetooth",
    "PySide6.QtPositioning", "PySide6.QtSerialPort", "PySide6.QtTest",
    "PySide6.QtDesigner", "PySide6.QtHelp", "PySide6.QtOpenGL",
    "PySide6.QtOpenGLWidgets", "PySide6.QtPdf", "PySide6.QtPdfWidgets",
    "PySide6.QtSpatialAudio", "PySide6.QtTextToSpeech", "PySide6.QtWebChannel",
    "PySide6.QtWebSockets", "PySide6.QtSql", "PySide6.QtDBus", "PySide6.QtRemoteObjects",
]


def is_windows():
    return os.name == "nt"


def check_prerequisites():
    try:
        import PyInstaller  # noqa: F401
    except ImportError:
        print("packaging needs PyInstaller:\n\n    pip install pyinstaller\n", file=sys.stderr)
        return False
    missing = [name for name in ASSEMBLIES if name.endswith(".dll")
               and not os.path.exists(os.path.join(BUILD, name))]
    if missing:
        print("the IDE's .NET assemblies have not been built: " + ", ".join(missing)
              + "\n\n    dotnet build            (Windows, .NET 8 SDK)\n"
                "    tools/build.py --all    (a shell and the sandbox toolchain)\n",
              file=sys.stderr)
        return False
    return True


def pyinstaller_arguments(onefile, name, console=False):
    separator = ";" if is_windows() else ":"
    arguments = [
        sys.executable, "-m", "PyInstaller",
        "--noconfirm",
        "--name", name,
        "--distpath", DIST,
        "--workpath", os.path.join(BUILD, "pyinstaller"),
        "--specpath", BUILD,
        "--paths", ROOT,
        "--collect-submodules", "ide",
        # A window, not a console: on Windows a console flashes up behind an
        # application that has a terminal panel of its own.
        "--console" if console or not is_windows() else "--windowed",
        "--onefile" if onefile else "--onedir",
        "--exclude-module", "tkinter",
    ]
    for module in EXCLUDED_QT:
        arguments += ["--exclude-module", module]
    # The assemblies are copied after the build, not embedded: they are .NET
    # binaries with their own runtime config beside them, and PyInstaller would
    # flatten them into the archive where `dotnet` cannot read them.
    arguments.append(ENTRY)
    return arguments


def copy_assemblies(destination):
    """Put the .NET side of Studio where `runtime.assembly()` looks for it."""
    target = os.path.join(destination, "build")
    os.makedirs(target, exist_ok=True)
    copied = []
    for name in ASSEMBLIES:
        source = os.path.join(BUILD, name)
        if os.path.exists(source):
            shutil.copy2(source, os.path.join(target, name))
            copied.append(name)
    return copied


def main(argv):
    parser = argparse.ArgumentParser(description="Package TERN Studio.")
    parser.add_argument("--onefile", action="store_true",
                        help="a single executable instead of a folder (slower start)")
    parser.add_argument("--name", default="TERN Studio", help="application name")
    parser.add_argument("--clean", action="store_true", help="discard previous build output")
    parser.add_argument("--console", action="store_true",
                        help="keep a console window (useful when a packaged build misbehaves)")
    args = parser.parse_args(argv)

    if not check_prerequisites():
        return 1

    if args.clean:
        for path in (os.path.join(BUILD, "pyinstaller"),
                     os.path.join(BUILD, args.name + ".spec"),
                     os.path.join(DIST, args.name),
                     os.path.join(DIST, args.name + ".exe")):
            shutil.rmtree(path, ignore_errors=True) if os.path.isdir(path) else (
                os.path.exists(path) and os.remove(path))

    print("==> packaging %s with PyInstaller" % args.name)
    finished = subprocess.run(
        pyinstaller_arguments(args.onefile, args.name, args.console), cwd=ROOT)
    if finished.returncode != 0:
        return finished.returncode

    if args.onefile:
        target = os.path.join(DIST, args.name + (".exe" if is_windows() else ""))
        print("\nbuilt %s" % target)
        print("The .NET assemblies are not inside a one-file build; put them in a "
              "`build` folder beside it, or use the folder build.")
        return 0

    folder = os.path.join(DIST, args.name)
    copied = copy_assemblies(folder)
    print("\nbuilt %s" % folder)
    if copied:
        print("copied %s into %s" % (", ".join(copied), os.path.join(folder, "build")))
    else:
        print("warning: no assemblies copied; language features will not work")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
