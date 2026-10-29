#!/usr/bin/env python3
"""Compile the C# baseline in bench/compare with the same Roslyn pipeline that
builds the interpreter, so the comparison uses one toolchain and one runtime.

Result: build/csharp-bench/Bench.dll (+ runtimeconfig) and a `dotnet` launcher.
"""
import os
import sys
from pathlib import Path

WORK = Path(os.environ.get("TERN_TOOLCHAIN_DIR", "/tmp/tern-toolchain"))
DOTNET = WORK / "dotnet"
TOOLS = DOTNET / "tools"
FX = DOTNET / "shared" / "Microsoft.NETCore.App"
REPO = Path(__file__).resolve().parent.parent
OUT = REPO / "build" / "csharp-bench"

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

for name in ("Microsoft.CodeAnalysis.CSharp.dll", "Microsoft.CodeAnalysis.dll"):
    clr.AddReference(str(TOOLS / name))

from Microsoft.CodeAnalysis import (  # noqa: E402
    MetadataReference,
    NullableContextOptions,
    OptimizationLevel,
    OutputKind,
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


def main() -> int:
    import shutil

    OUT.mkdir(parents=True, exist_ok=True)
    source = (REPO / "bench" / "compare" / "BenchCsharp.cs").read_text(encoding="utf-8")

    parse_options = CSharpParseOptions(LanguageVersion.Preview)
    from Microsoft.CodeAnalysis import SyntaxTree  # noqa: E402

    tree_set = List[SyntaxTree]()
    tree_set.Add(CSharpSyntaxTree.ParseText(source, parse_options, "BenchCsharp.cs"))

    framework_dir = next(FX.iterdir())
    refs = List[MetadataReference]()
    for dll in sorted(framework_dir.glob("*.dll")):
        if dll.name.split(".")[0] in ("System", "mscorlib", "netstandard", "Microsoft", "WindowsBase"):
            refs.Add(MetadataReference.CreateFromFile(str(dll)))

    options = CSharpCompilationOptions(OutputKind.ConsoleApplication)
    options = options.WithOptimizationLevel(OptimizationLevel.Release)
    options = options.WithNullableContextOptions(NullableContextOptions.Enable)
    options = options.WithAllowUnsafe(True)

    compilation = CSharpCompilation.Create("Bench", tree_set, refs, options)
    stream = MemoryStream()
    result = compilation.Emit(stream)

    errors = [d for d in result.Diagnostics if str(d.Severity) == "Error"]
    for diagnostic in errors:
        print(diagnostic)
    if not result.Success:
        print(f"build FAILED: {len(errors)} error(s)")
        return 1

    (OUT / "Bench.dll").write_bytes(bytes(stream.ToArray()))
    (OUT / "Bench.runtimeconfig.json").write_text(
        '{\n  "runtimeOptions": {\n    "tfm": "net10.0",\n'
        '    "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.11" },\n'
        '    "configProperties": {\n'
        '      "System.Runtime.TieredPGO": true,\n'
        '      "System.Runtime.TieredCompilation.QuickJitForLoops": false\n'
        '    }\n  }\n}\n',
        encoding="utf-8",
    )
    shutil.copyfile(DOTNET / "dotnet", OUT / "dotnet")
    os.chmod(OUT / "dotnet", 0o755)
    print(f"build OK: build/csharp-bench/Bench.dll")
    return 0


if __name__ == "__main__":
    sys.exit(main())
