#!/usr/bin/env bash
# Bootstraps a self-contained C# toolchain that does not need the .NET SDK.
#
# Why: this sandbox has no `dotnet` SDK, but it does have npm/pypi. The npm
# package `@dprint/roslyn-linux-x64-glibc` ships a complete self-contained
# .NET runtime *and* Microsoft.CodeAnalysis (the Roslyn C# compiler) as plain
# assemblies. We reassemble those into a normal `dotnet` install layout
# (host/fxr + shared/Microsoft.NETCore.App) and drive Roslyn from Python via
# pythonnet. A tiny C launcher (hostfxr) then runs compiled assemblies as real
# native processes.
#
# If you already have the .NET SDK installed you do not need any of this:
#   dotnet build && dotnet run --project TernRunner -- script.tern
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="${TERN_TOOLCHAIN_DIR:-/tmp/tern-toolchain}"
VERSION_MARKER="$WORK/.ready"

if [[ -f "$VERSION_MARKER" ]]; then
  echo "toolchain already present at $WORK"
  exit 0
fi

echo "==> bootstrapping toolchain in $WORK"
rm -rf "$WORK"
mkdir -p "$WORK/pkg" "$WORK/dotnet/host/fxr" "$WORK/dotnet/shared/Microsoft.NETCore.App" \
         "$WORK/dotnet/tools" "$WORK/hello"

pushd "$WORK/pkg" >/dev/null
npm pack @dprint/roslyn-linux-x64-glibc --silent >/dev/null
tar xzf ./*.tgz
popd >/dev/null

PKG="$WORK/pkg/package"
RUNTIME_VERSION="$(python3 - "$PKG" <<'PY'
import json,sys
cfg=json.load(open(sys.argv[1]+"/dprint-plugin-roslyn.runtimeconfig.json"))
print(cfg["runtimeOptions"]["includedFrameworks"][0]["version"])
PY
)"
RID_PATH="$WORK/dotnet/shared/Microsoft.NETCore.App/$RUNTIME_VERSION"
mkdir -p "$RID_PATH"

mkdir -p "$WORK/dotnet/host/fxr/$RUNTIME_VERSION"
cp "$PKG/libhostfxr.so" "$WORK/dotnet/host/fxr/$RUNTIME_VERSION/"

# Runtime assemblies + native libraries (everything that is not app-specific).
for f in "$PKG"/*.dll "$PKG"/*.so "$PKG"/createdump; do
  case "$(basename "$f")" in
    Microsoft.CodeAnalysis*|Newtonsoft.Json.dll|dprint-plugin-roslyn.dll) continue ;;
  esac
  cp "$f" "$RID_PATH/"
done
cp "$PKG"/Microsoft.CodeAnalysis*.dll "$WORK/dotnet/tools/"

printf '%s' "$RUNTIME_VERSION" > "$RID_PATH/.version"

# hostfxr refuses to resolve a framework that has no deps.json, so synthesize
# one describing the flat layout we just created.
python3 - "$RID_PATH" "$RUNTIME_VERSION" <<'PY'
import json, os, sys
d, version = sys.argv[1], sys.argv[2]
dlls = sorted(f for f in os.listdir(d) if f.endswith(".dll"))
native = sorted(f for f in os.listdir(d) if f.endswith(".so") or f == "createdump")
lib = "Microsoft.NETCore.App/" + version
deps = {
    "runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/linux-x64", "signature": ""},
    "compilationOptions": {},
    "targets": {".NETCoreApp,Version=v10.0/linux-x64": {lib: {
        "runtime": {f: {} for f in dlls},
        "native": {f: {} for f in native}}}},
    "libraries": {lib: {"type": "package", "serviceable": True, "sha512": ""}},
}
with open(os.path.join(d, "Microsoft.NETCore.App.deps.json"), "w") as h:
    json.dump(deps, h, indent=1)
PY

# pythonnet drives Roslyn; the venv keeps it isolated from the system python.
python3 -m venv "$WORK/venv"
"$WORK/venv/bin/pip" install --quiet pythonnet

cat > "$WORK/fx.runtimeconfig.json" <<EOF
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "$RUNTIME_VERSION" },
    "configProperties": {
      "System.GC.Server": false,
      "System.Runtime.TieredPGO": true
    }
  }
}
EOF

# Native launcher: `dotnet <app.dll> [args...]` equivalent built on hostfxr.
cat > "$WORK/launcher.c" <<'EOF'
#include <stdio.h>
#include <stdlib.h>
#include <glob.h>
#include <dlfcn.h>

typedef int (*init_cl_fn)(int, const char **, const void *, void **);
typedef int (*run_app_fn)(void *);
typedef int (*close_fn)(void *);
typedef void (*err_writer_fn)(const char *);
typedef err_writer_fn (*set_err_writer_fn)(err_writer_fn);

static void err_writer(const char *m) { fputs(m, stderr); }

int main(int argc, char **argv) {
    if (argc < 2) { fprintf(stderr, "usage: %s app.dll [args...]\n", argv[0]); return 1; }
    const char *root = getenv("DOTNET_ROOT");
    if (!root) { fprintf(stderr, "DOTNET_ROOT must be set\n"); return 1; }
    char pattern[4096];
    snprintf(pattern, sizeof pattern, "%s/host/fxr/*/libhostfxr.so", root);
    glob_t g;
    if (glob(pattern, 0, NULL, &g) != 0 || g.gl_pathc == 0) { fprintf(stderr, "hostfxr not found\n"); return 1; }
    void *lib = dlopen(g.gl_pathv[g.gl_pathc - 1], RTLD_LAZY | RTLD_GLOBAL);
    globfree(&g);
    if (!lib) { fprintf(stderr, "dlopen: %s\n", dlerror()); return 1; }
    init_cl_fn init = (init_cl_fn)dlsym(lib, "hostfxr_initialize_for_dotnet_command_line");
    run_app_fn run = (run_app_fn)dlsym(lib, "hostfxr_run_app");
    close_fn close_ctx = (close_fn)dlsym(lib, "hostfxr_close");
    set_err_writer_fn sew = (set_err_writer_fn)dlsym(lib, "hostfxr_set_error_writer");
    if (!init || !run) { fprintf(stderr, "hostfxr symbols missing\n"); return 1; }
    if (sew) sew(err_writer);
    void *ctx = NULL;
    int rc = init(argc - 1, (const char **)(argv + 1), NULL, &ctx);
    if (rc != 0) { fprintf(stderr, "hostfxr init failed: 0x%x\n", rc); return 1; }
    rc = run(ctx);
    if (close_ctx) close_ctx(ctx);
    return rc;
}
EOF
gcc -O2 -o "$WORK/dotnet/dotnet" "$WORK/launcher.c" -ldl

touch "$VERSION_MARKER"
echo "==> toolchain ready: $WORK/dotnet (runtime $RUNTIME_VERSION)"
