#!/usr/bin/env python3
"""Checks that both back ends offer the same standard library, on every platform.

The interpreter registers its namespaces in C#; the compiled runtime registers
them in `namespace_map` in `native/runtime/tern_rt.c`, under a platform split.
That split is where the two bugs this file exists for came from:

* `d3d12` was written inside the `#else` — the non-Windows half — so a Windows
  binary had no `d3d12` at all and `d3d12.available()` said "map has no method
  'available'". Nothing on Linux could notice: the Linux branch was correct,
  and `tools/test-windows.sh` only proves that a Windows binary builds, never
  that it runs.
* `net.receiveSome` was added to the real namespace and not to the Windows stub
  list, so the same call failed on Windows for a different reason.

So there are three things to check, none of which needs Windows:

1. every namespace the interpreter has, the runtime has, with the same members;
2. every namespace answers on both platforms — either with a real implementation
   outside the platform split, or with a stub inside the `_WIN32` half that
   lists every member the real one has;
3. the stub lists and the real lists match exactly.

    python3 tools/test_namespaces.py          run it
    python3 tools/test_namespaces.py -v       show each check
"""

import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
RUNTIME = os.path.join(ROOT, "native", "runtime", "tern_rt.c")
CLI = [os.path.join(ROOT, "tools", "tern")] if os.name != "nt" else [
    "cmd", "/c", os.path.join(ROOT, "tools", "tern.cmd")]

# The modules a Windows binary cannot have: they need Winsock or EGL, and the
# translations that would give them Winsock do not exist yet. `d3d12` is not on
# the list — it is the one graphics module Windows is supposed to have.
EXPECTED_WINDOWS_STUBS = {"net", "http", "rcon", "sftp", "gl"}


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
                for line in str(detail).splitlines():
                    print(f"       {line}")


# --------------------------------------------------------------------- sources

def interpreter_namespaces():
    """`tern docs`: {namespace: {member, ...}} for the engine as built."""
    result = subprocess.run(CLI + ["docs"], cwd=ROOT, capture_output=True, text=True, timeout=300)
    if result.returncode != 0:
        raise SystemExit("tern docs failed: " + (result.stderr or result.stdout)[-400:])

    # A namespace section is `## `name``; methods-by-type sections are `###`.
    sections = re.split(r"^## (.+)$", result.stdout, flags=re.M)
    spaces = {}
    for i in range(1, len(sections), 2):
        title, chunk = sections[i].strip(), sections[i + 1]
        match = re.fullmatch(r"`([a-z0-9]+)`", title)
        if match:
            spaces[match.group(1)] = set(
                re.findall(r"^\| `([A-Za-z][A-Za-z0-9]*)` \|", chunk, flags=re.M))
    return spaces


def runtime_namespaces():
    """`namespace_map` in the runtime, split by the preprocessor branches.

    Returns (shared, windows_stubs, posix_only, all_modules):
      shared        registered outside the platform split, so on both
      windows_stubs what a Windows binary answers for a missing module
      posix_only    registered only in the `#else` — invisible on Windows
    """
    source = open(RUNTIME, encoding="utf-8").read()
    start = source.index('static Value namespace_map(const char *ns) {')
    end = source.index('/// Looks a function up in the well known namespaces', start)
    body = source[start:end]

    windows_at = body.index('#ifdef _WIN32')
    else_at = body.index('#else', windows_at)
    endif_at = body.index('#endif', else_at)

    def parse(text):
        found = {}
        parts = re.split(r'if \(strcmp\(ns, "([a-z0-9]+)"\) == 0\) \{', text)
        for i in range(1, len(parts), 2):
            name, chunk = parts[i], parts[i + 1]
            stub = re.search(r'static const char \*names\[\] = \{(.*?)\};', chunk, re.S)
            if stub:
                found[name] = set(re.findall(r'"([A-Za-z][A-Za-z0-9]*)"', stub.group(1)))
            else:
                found[name] = set(re.findall(r'str_from_cstr\("([A-Za-z][A-Za-z0-9]*)"\)', chunk))
        return found

    shared = {}
    shared.update(parse(body[:windows_at]))
    shared.update(parse(body[endif_at:]))
    stubs = parse(body[windows_at:else_at])
    posix = parse(body[else_at:endif_at])
    everywhere = dict(shared)
    everywhere.update(posix)
    return shared, stubs, posix, everywhere


# ---------------------------------------------------------------------- checks

def test_namespaces(checks):
    interpreter = interpreter_namespaces()
    shared, stubs, posix_only, native = runtime_namespaces()

    # If the parser stops matching the source, every check below would pass on
    # two empty dictionaries.
    checks.check("the runtime's namespace map was found",
                 len(native) >= 14 and len(interpreter) >= 14,
                 f"parsed {len(native)} native and {len(interpreter)} interpreter namespaces")
    if not native or not interpreter:
        return

    checks.check("the interpreter and the runtime register the same namespaces",
                 set(interpreter) == set(native),
                 "only in the interpreter: " + ", ".join(sorted(set(interpreter) - set(native))) +
                 "\nonly in the runtime: " + ", ".join(sorted(set(native) - set(interpreter))))

    for name in sorted(set(interpreter) & set(native)):
        missing = sorted(interpreter[name] - native[name])
        extra = sorted(native[name] - interpreter[name])
        checks.check(f"{name} has the same members in both back ends", not missing and not extra,
                     ("missing from the compiled runtime: " + ", ".join(missing) if missing else "") +
                     ("\nonly in the compiled runtime: " + ", ".join(extra) if extra else ""))

    checks.check("the modules Windows cannot have are the expected ones",
                 set(stubs) == EXPECTED_WINDOWS_STUBS,
                 "stubbed: " + ", ".join(sorted(stubs)) +
                 "\nexpected: " + ", ".join(sorted(EXPECTED_WINDOWS_STUBS)))

    # The bug the user hit: a module implemented only in the non-Windows branch
    # is not on Windows at all, and an empty namespace map reads as "map has no
    # method 'available'" rather than as a missing module.
    orphans = sorted(set(posix_only) - set(stubs))
    checks.check("no module is missing from the Windows build", not orphans,
                 "implemented only for non-Windows and not stubbed: " + ", ".join(orphans))

    for name in sorted(stubs):
        missing = sorted(native.get(name, set()) - stubs[name])
        checks.check(f"the Windows stub for {name} lists every member", not missing,
                     "absent from the stub list: " + ", ".join(missing))


def main(argv):
    verbose = "-v" in argv or "--verbose" in argv
    checks = Checks(verbose)
    test_namespaces(checks)
    print()
    print(f"passed: {checks.passed}  failed: {len(checks.failed)}")
    for name in checks.failed:
        print(f"  failed: {name}")
    return 1 if checks.failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
