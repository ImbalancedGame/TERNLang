#!/usr/bin/env python3
"""Compile the TERN solution with Roslyn and emit build/TERN.Runner.dll.

Also builds the IDE's syntax server (build/TERN.Ide.dll) when asked for with
`--ide`, or both with `--all`.

This is the sandbox-friendly equivalent of `dotnet build`: it uses the
Microsoft.CodeAnalysis assemblies recovered by setup-toolchain.sh, driven from
Python through pythonnet. On a machine with the .NET SDK you can ignore this
script entirely and run `dotnet build`.
"""
import os
import sys
import glob
import shutil
from pathlib import Path

WORK = Path(os.environ.get("TERN_TOOLCHAIN_DIR", "/tmp/tern-toolchain"))
DOTNET = WORK / "dotnet"
TOOLS = DOTNET / "tools"
FX = DOTNET / "shared" / "Microsoft.NETCore.App"
REPO = Path(__file__).resolve().parent.parent
OUT = REPO / "build"

if not (WORK / ".ready").exists():
    sys.exit("toolchain missing: run tools/setup-toolchain.sh first")

os.environ["DOTNET_ROOT"] = str(DOTNET)

import clr_loader  # noqa: E402
from pythonnet import set_runtime  # noqa: E402

runtime = clr_loader.get_coreclr(
    runtime_config=str(WORK / "fx.runtimeconfig.json"), dotnet_root=str(DOTNET)
)
set_runtime(runtime)

import clr  # noqa: E402
import System  # noqa: E402

for name in ("Microsoft.CodeAnalysis.CSharp.dll", "Microsoft.CodeAnalysis.dll"):
    clr.AddReference(str(TOOLS / name))

from Microsoft.CodeAnalysis import (  # noqa: E402
    MetadataReference,
    OutputKind,
    SyntaxTree,
    DiagnosticSeverity,
    OptimizationLevel,
)
from Microsoft.CodeAnalysis.CSharp import (  # noqa: E402
    CSharpCompilation,
    CSharpCompilationOptions,
    CSharpParseOptions,
    CSharpSyntaxTree,
    LanguageVersion,
)
from System.Collections.Generic import List  # noqa: E402
from System.IO import MemoryStream  # noqa: E402


# target -> (assembly name, source patterns). Both targets share the language
# itself and differ only in the program on top of it.
TARGETS = {
    "runner": ("TERN.Runner", ("TERN/**/*.cs", "TernRunner/**/*.cs")),
    "ide": ("TERN.Ide", ("TERN/**/*.cs", "ide/Server/**/*.cs")),
}


def sources(patterns):
    files = []
    for pattern in patterns:
        files.extend(sorted(REPO.glob(pattern)))
    return [f for f in files if "/obj/" not in f.as_posix() and "/bin/" not in f.as_posix()]


def reference_assemblies():
    framework_dir = next(FX.iterdir())
    refs = []
    for dll in sorted(framework_dir.glob("*.dll")):
        stem = dll.name.split(".")[0]
        if stem in ("System", "mscorlib", "netstandard", "Microsoft", "WindowsBase"):
            refs.append(dll)
    return refs


def build(assembly, patterns, release):
    """Compiles one target. Returns True on success."""
    trees = List[SyntaxTree]()
    parse_options = CSharpParseOptions(LanguageVersion.Preview)
    for path in sources(patterns):
        text = path.read_text(encoding="utf-8")
        trees.Add(
            CSharpSyntaxTree.ParseText(
                text, parse_options, path.relative_to(REPO).as_posix()
            )
        )

    refs = List[MetadataReference]()
    for dll in reference_assemblies():
        refs.Add(MetadataReference.CreateFromFile(str(dll)))

    options = CSharpCompilationOptions(OutputKind.ConsoleApplication)
    options = options.WithOptimizationLevel(
        OptimizationLevel.Release if release else OptimizationLevel.Debug
    )
    from Microsoft.CodeAnalysis import NullableContextOptions  # noqa: E402

    options = options.WithNullableContextOptions(NullableContextOptions.Enable)
    options = options.WithAllowUnsafe(True)
    options = options.WithConcurrentBuild(True)

    compilation = CSharpCompilation.Create(assembly, trees, refs, options)

    stream = MemoryStream()
    result = compilation.Emit(stream)

    errors = 0
    warnings = 0
    for diag in result.Diagnostics:
        severity = str(diag.Severity)
        if severity in ("Error", "Warning"):
            if severity == "Error":
                errors += 1
            else:
                warnings += 1
            print(diag)
    if not result.Success:
        print(f"\nbuild FAILED: {assembly}.dll, {errors} error(s), {warnings} warning(s)")
        return False

    (OUT / f"{assembly}.dll").write_bytes(bytes(stream.ToArray()))
    (OUT / f"{assembly}.runtimeconfig.json").write_text(
        '{\n  "runtimeOptions": {\n    "tfm": "net10.0",\n'
        '    "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.11" },\n'
        '    "configProperties": {\n'
        '      "System.Runtime.TieredPGO": true,\n'
        '      "System.Runtime.TieredCompilation.QuickJitForLoops": true\n'
        '    }\n  }\n}\n',
        encoding="utf-8",
    )
    shutil.copyfile(DOTNET / "dotnet", OUT / "dotnet")
    os.chmod(OUT / "dotnet", 0o755)
    size = (OUT / f"{assembly}.dll").stat().st_size
    print(f"build OK: build/{assembly}.dll ({size/1024:.1f} KiB), {warnings} warning(s)")
    return True


def main() -> int:
    release = "--debug" not in sys.argv
    OUT.mkdir(exist_ok=True)

    if "--all" in sys.argv:
        wanted = ["runner", "ide"]
    elif "--ide" in sys.argv:
        wanted = ["ide"]
    else:
        wanted = ["runner"]

    status = 0
    for target in wanted:
        assembly, patterns = TARGETS[target]
        if not build(assembly, patterns, release):
            status = 1

    return status


if __name__ == "__main__":
    sys.exit(main())
