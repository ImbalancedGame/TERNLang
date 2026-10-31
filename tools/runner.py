"""How to start the TERN command-line interface, on any platform.

`tools/tern` is a three-line bash script. That is fine from a shell on a Unix
machine and useless everywhere else: Windows has no bash, and a Python process
handing it to `subprocess` gets

    OSError: [WinError 193] %1 is not a valid Win32 application

because CreateProcess is asked to execute a text file. Every tool in `native/`
used to assume that script, so on Windows the native backend could emit
nothing at all.

The interface itself is an assembly. It can be started directly:

    dotnet build/TERN.Runner.dll run examples/wordcount.tern

and what this module adds is working out which `dotnet` and which assembly,
and whether that pair needs `DOTNET_ROOT` pointing at a bundled runtime. The
answer lives in `ide/Studio/runtime.py`, which is stdlib-only and Qt-free, and
is loaded here by path so that `tools/` does not have to be a package and
neither side depends on the other's import layout.

    from tools import runner
    finished = runner.run(REPO, ["run", "examples/wordcount.tern"])

    runner.command(REPO)   # ['/tmp/.../dotnet', '/repo/build/TERN.Runner.dll']
    runner.environment()   # {'DOTNET_ROOT': '/tmp/tern-toolchain/dotnet'}

Set `TERN_CLI` to override the choice with any executable that takes the same
arguments — a release build, a wrapper script, a different runtime.
"""

import importlib.util
import os
import shlex
import subprocess

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WINDOWS = os.name == "nt"


def _runtime_module():
    """`ide/Studio/runtime.py`, loaded by path.

    By path rather than by name: `tools/` is not a package, and importing
    `ide.Studio` would put the repository root on `sys.path` as a side effect
    of what is meant to be a lookup.
    """
    path = os.path.join(ROOT, "ide", "Studio", "runtime.py")
    spec = importlib.util.spec_from_file_location("_tern_runtime", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_runtime = _runtime_module()


def command(root):
    """The argv prefix that runs the CLI: a list, ready to extend.

    Checked in order:

    1. `TERN_CLI`, split like a shell word list, so a command with arguments
       works;
    2. a `dotnet` with `build/TERN.Runner.dll` beside it — the bundled sandbox
       runtime first, then the SDK on `PATH`;
    3. `tools/tern` (or `tern.cmd`), for a checkout with a launcher but no
       assembly this process can find;
    4. bare `tern` on `PATH`.
    """
    configured = os.environ.get("TERN_CLI")
    if configured:
        return shlex.split(configured, posix=not WINDOWS)

    found = _runtime.command(root, "TERN.Runner")
    if found:
        return found

    script = os.path.join(root, "tools", "tern" + (".cmd" if WINDOWS else ""))
    if os.path.exists(script):
        if WINDOWS:
            # A batch file is not an executable: something has to interpret it.
            return [os.environ.get("COMSPEC", "cmd.exe"), "/c", script]
        return [script]

    return ["dotnet", os.path.join(root, "build", "TERN.Runner.dll")]


def environment(root, argv=None):
    """Variables the chosen launcher needs, usually just `DOTNET_ROOT`."""
    argv = command(root) if argv is None else argv
    return _runtime.environment(root, argv[0])


def run(root, arguments, **options):
    """`subprocess.run` for the CLI, with the environment already sorted out."""
    argv = command(root)
    environment_variables = os.environ.copy()
    environment_variables.update(environment(root, argv))
    environment_variables.update(options.pop("env", None) or {})
    return subprocess.run([*argv, *arguments], env=environment_variables, **options)
