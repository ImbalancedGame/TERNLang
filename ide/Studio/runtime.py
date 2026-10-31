"""Where the .NET runtime and the compiled assemblies are, on any platform.

Two ways to have a .NET to run the servers with:

* the **bundled toolchain** the sandbox bootstrap builds — a `dotnet`
  executable copied next to the assemblies in `build/`, with the runtime in
  `/tmp/tern-toolchain` (or `$TERN_TOOLCHAIN_DIR`);
* a **normal SDK install**, where `dotnet` is on `PATH` and `dotnet build`
  writes each project's output under its own `bin/`.

The rest of the IDE should not care which. Everything here is plain `os` and
`shutil`, because `tools/` uses it too and `tools/` cannot depend on Qt.
"""

import glob
import os
import shutil

WINDOWS = os.name == "nt"

# On Windows the host is dotnet.exe; everywhere else it has no suffix.
EXE = ".exe" if WINDOWS else ""


def toolchain_directory():
    """The .NET root the sandbox bootstrap creates, if it is configured."""
    return os.environ.get("TERN_TOOLCHAIN_DIR") or os.path.join(
        os.environ.get("TEMP", "/tmp") if WINDOWS else "/tmp", "tern-toolchain")


def _exists(path):
    return bool(path) and os.path.exists(path)


def dotnet(install_root):
    """The `dotnet` executable to run an assembly with, or None.

    Order matters: the bundled launcher next to the assemblies wins, because on
    a machine with both it is the one the code was built against.
    """
    bundled = [
        os.path.join(install_root, "build", "dotnet" + EXE),
        os.path.join(install_root, "build", "dotnet"),
    ]
    toolchain = [
        os.path.join(toolchain_directory(), "dotnet", "dotnet" + EXE),
        os.path.join(toolchain_directory(), "dotnet", "dotnet"),
    ]
    for candidate in bundled + toolchain:
        if _exists(candidate) and os.access(candidate, os.X_OK):
            return candidate

    # A system install. `dotnet build` output is not run through the bundled
    # launcher, so this is the normal case for anyone who built with the SDK.
    found = shutil.which("dotnet")
    return found


def uses_bundled_runtime(install_root, executable):
    """True when `executable` is the bundled launcher rather than an SDK host.

    A system `dotnet` finds its own runtime; pointing DOTNET_ROOT at the
    sandbox's copy would break it, so only the bundled one gets the variable.
    """
    if not executable:
        return False
    build = os.path.abspath(os.path.join(install_root, "build"))
    return os.path.dirname(os.path.abspath(executable)) == build


def environment(install_root, executable=None):
    """Extra environment variables needed to start a server."""
    executable = executable or dotnet(install_root)
    if uses_bundled_runtime(install_root, executable):
        return {"DOTNET_ROOT": os.path.join(toolchain_directory(), "dotnet")}
    return {}


def assembly(install_root, name, configuration="Release"):
    """The built assembly called `name` (without `.dll`), or None.

    Checks the flat `build/` directory the toolchain build writes, then the
    per-project `bin/` trees an SDK build writes.
    """
    flat = os.path.join(install_root, "build", name + ".dll")
    if _exists(flat):
        return flat

    # A build that honours Configuration lands one level down; anything under
    # build/ is still "the build", wherever the SDK decided to put it.
    under_build = sorted(glob.glob(
        os.path.join(install_root, "build", "**", name + ".dll"), recursive=True))
    if under_build:
        return under_build[0]

    patterns = [
        os.path.join(install_root, "**", "bin", configuration, "**", name + ".dll"),
        os.path.join(install_root, "**", "bin", "**", name + ".dll"),
    ]
    for pattern in patterns:
        found = sorted(glob.glob(pattern, recursive=True))
        found = [path for path in found if os.sep + "obj" + os.sep not in path]
        if found:
            return found[0]
    return None


def runner(install_root):
    """How to run a TERN program: `[dotnet, TERN.Runner.dll]`, or the shell
    launcher `tools/tern` when that is what exists.

    The shell launcher is a bash script, so it is only a fallback for a machine
    that has bash and no directly runnable assembly.
    """
    found = command(install_root, "TERN.Runner")
    if found:
        return found
    for base in (install_root, os.getcwd()):
        script = os.path.join(base, "tools", "tern")
        if _exists(script):
            return [script]
    return ["tern"]


def command(install_root, name):
    """`[dotnet, assembly]` for a server, or None when either is missing."""
    executable = dotnet(install_root)
    path = assembly(install_root, name)
    if not executable or not path:
        return None
    return [executable, path]
