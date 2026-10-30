#!/usr/bin/env python3
"""Generates the MSDN-style reference under docs/msdn/.

Every page is built from two sources that have to agree:

  * `./tools/tern docs --json` — the catalogue the engine actually exposes, with
    each function's arity and one-line description;
  * tools/reference/annotations*.py — the prose: parameters, return value,
    errors, remarks, examples.

The catalogue decides what exists, so a function that was removed cannot keep a
page; the annotations decide what it means. A catalogue entry with no
annotation still gets a page, built from the engine's own description, and the
generator lists it — better a thin page than a missing one, and the list is the
work left to do.

    python3 tools/gen-reference.py            writes docs/msdn/
    python3 tools/gen-reference.py --check    fails if the tree is stale
"""

import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "docs", "msdn")

sys.path.insert(0, os.path.join(HERE, "reference"))
import annotations          # noqa: E402
import annotations_core     # noqa: E402
import annotations_methods  # noqa: E402

NAMESPACES = dict(annotations.NAMESPACES)
NAMESPACES.update(annotations_core.NAMESPACES)

FUNCTIONS = dict(annotations.FUNCTIONS)
FUNCTIONS.update(annotations_core.FUNCTIONS)

METHODS = annotations_methods.METHODS

TYPE_TITLES = {
    "string": "String methods",
    "list": "List methods",
    "map": "Map methods",
    "range": "Range methods",
    "int": "Integer methods",
    "float": "Float methods",
}


def catalogue():
    """The engine's own description of the standard library."""
    result = subprocess.run(
        [os.path.join(ROOT, "tools", "tern"), "docs", "--json"],
        cwd=ROOT, capture_output=True, text=True, check=True)
    return json.loads(result.stdout)


def signature(name, params, minimum, maximum, receiver=None):
    """Renders `name(a, b)` with the variadic and optional markers."""
    rendered = []
    for index, (param, _type, _text) in enumerate(params):
        label = param
        optional = index >= minimum
        if maximum == -1 and index == len(params) - 1:
            label += "..."
        elif optional:
            label += "?"
        rendered.append(label)
    head = f"{receiver}.{name}" if receiver else name
    return f"{head}({', '.join(rendered)})"


def fallback_params(minimum, maximum):
    """Parameter names for an entry with no annotation: `arg1`, `arg2`, ..."""
    count = minimum if maximum == -1 else maximum
    params = [(f"arg{i + 1}", "any", "The value the function documents itself as taking.")
              for i in range(max(0, count))]
    return params


def table(headers, rows):
    if not rows:
        return "_None._\n"
    out = ["| " + " | ".join(headers) + " |",
           "| " + " | ".join("---" for _ in headers) + " |"]
    for row in rows:
        out.append("| " + " | ".join(cell.replace("|", "\\|") for cell in row) + " |")
    return "\n".join(out) + "\n"


def code_block(lines):
    return "```tern\n" + "\n".join(lines) + "\n```\n"


def function_page(group, entry, notes):
    name = entry["name"]
    full = f"{group}.{name}" if group != "global" else name
    note = notes.get(f"{group}.{name}", {})
    summary = note.get("summary") or entry["documentation"] or "No description yet."
    params = note.get("params") or fallback_params(entry["min"], entry["max"])
    returns = note.get("returns")
    errors = note.get("errors") or []
    remarks = note.get("remarks")
    example = note.get("example")
    see = note.get("see") or []

    lines = [f"# {full}", "", summary, "", "## Syntax", "", "```tern",
             signature(full, params, entry["min"], entry["max"]), "```", ""]
    lines += ["## Parameters", ""]
    rows = [(f"`{p}`", f"`{t}`", d) for p, t, d in params]
    lines.append(table(["Parameter", "Type", "Description"], rows))
    lines += ["## Return value", ""]
    if returns:
        kind, text = returns
        lines += [f"- Type: `{kind}`", f"- {text}", ""]
    else:
        lines += ["`nil`.", ""]
    if remarks:
        lines += ["## Remarks", "", remarks.strip(), ""]
    if errors:
        lines += ["## Errors", "",
                  table(["Error", "Raised when"], [(f"`{e}`", d) for e, d in errors])]
    if example:
        lines += ["## Example", "", code_block(example)]
    if see:
        lines += ["## See also", ""]
        for other in see:
            lines.append(f"- [{other}]({link_for(other, group)})")
        lines.append("")
    lines += ["---", "", f"Part of the [`{group}`](index.md) namespace."]
    return "\n".join(lines) + "\n"


def method_page(kind, entry, notes):
    name = entry["name"]
    key = f"{kind}.{name}"
    note = notes.get(key, {})
    summary = note.get("summary") or entry["documentation"] or "No description yet."
    params = note.get("params") or []
    # One hidden receiver plus the explicit parameters.
    minimum = max(0, entry["min"] - 1)
    maximum = entry["max"] - 1 if entry["max"] != -1 else -1
    returns = note.get("returns")
    errors = note.get("errors") or []
    remarks = note.get("remarks")
    example = note.get("example")
    see = note.get("see") or []

    lines = [f"# {kind}.{name}", "", summary, "", "## Syntax", "", "```tern",
             signature(name, params, minimum, maximum, receiver=kind), "```", ""]
    lines += ["`receiver` is the " + kind + " on the left of the dot.", ""]
    if params:
        lines += ["## Parameters", ""]
        rows = [(f"`{p}`", f"`{t}`", d) for p, t, d in params]
        lines.append(table(["Parameter", "Type", "Description"], rows))
    lines += ["## Return value", ""]
    if returns:
        kind_name, text = returns
        lines += [f"- Type: `{kind_name}`", f"- {text}", ""]
    else:
        lines += ["`nil`.", ""]
    if remarks:
        lines += ["## Remarks", "", remarks.strip(), ""]
    if errors:
        lines += ["## Errors", "",
                  table(["Error", "Raised when"], [(f"`{e}`", d) for e, d in errors])]
    if example:
        lines += ["## Example", "", code_block(example)]
    if see:
        lines += ["## See also", ""]
        for other in see:
            lines.append(f"- [{other}]({link_for(other, kind)})")
        lines.append("")
    lines += ["---", "", f"Part of the [`{kind}` methods](index.md)."]
    return "\n".join(lines) + "\n"


def link_for(name, here):
    """Relative link from a page in directory `here` to `name`."""
    if "." in name:
        group, member = name.split(".", 1)
        if group == "global":
            group = "global"
        if group == here:
            return f"{member}.md"
        return f"../{group}/{member}.md"
    return f"{name}.md"


def namespace_index(group, info, entries, methods_for_group=None):
    title = info.get("title", group)
    lines = [f"# {title}", "", info.get("summary", ""), ""]
    if info.get("intro"):
        lines += [info["intro"].strip(), ""]
    lines += ["## Functions", ""]
    rows = []
    for entry in sorted(entries, key=lambda e: e["name"]):
        note = FUNCTIONS.get(f"{group}.{entry['name']}", {})
        description = note.get("summary") or entry["documentation"] or ""
        rows.append((f"[`{entry['name']}`]({entry['name']}.md)",
                     f"{entry['min']}..{'n' if entry['max'] == -1 else entry['max']}",
                     description))
    lines.append(table(["Function", "Arguments", "Description"], rows))
    if info.get("example"):
        lines += ["## Example", "", code_block(info["example"])]
    return "\n".join(lines) + "\n"


def type_index(kind, entries):
    lines = [f"# {TYPE_TITLES.get(kind, kind)}", "",
             f"Methods on a `{kind}` value, called with a dot: `value.{entries[0]['name']}(...)`" if entries
             else "", ""]
    rows = []
    for entry in sorted(entries, key=lambda e: e["name"]):
        note = METHODS.get(f"{kind}.{entry['name']}", {})
        description = note.get("summary") or entry["documentation"] or ""
        rows.append((f"[`{entry['name']}`]({entry['name']}.md)", description))
    lines.append(table(["Method", "Description"], rows))
    return "\n".join(lines) + "\n"


def render():
    """Builds the whole tree as {relative path: text}."""
    data = catalogue()
    files = {}

    groups = {}
    for entry in data["members"]:
        groups.setdefault(entry["group"], []).append(entry)

    kinds = {}
    for entry in data["methods"]:
        kinds.setdefault(entry["kind"], []).append(entry)

    covered = set()
    for group, entries in groups.items():
        info = NAMESPACES.get(group, {})
        for entry in entries:
            key = f"{group}.{entry['name']}"
            covered.add(key)
            files[f"{group}/{entry['name']}.md"] = function_page(group, entry, FUNCTIONS)
        files[f"{group}/index.md"] = namespace_index(group, info, entries)

    for kind, entries in kinds.items():
        for entry in entries:
            covered.add(f"{kind}.{entry['name']}")
            files[f"{kind}/{entry['name']}.md"] = method_page(kind, entry, METHODS)
        files[f"{kind}/index.md"] = type_index(kind, entries)

    # Index page.
    lines = ["# TERN standard library reference", "",
             "Every function, method and constant the language exposes, one page each: "
             "syntax, parameters, return value, remarks, errors, an example and the "
             "neighbouring calls.", "",
             "The pages are generated from the engine's own catalogue "
             "(`tools/tern docs --json`) and the prose in `tools/reference/`. "
             "`bash tools/gen-docs.sh` rewrites them.", "",
             "## Namespaces", ""]
    rows = []
    for group in sorted(groups):
        info = NAMESPACES.get(group, {})
        rows.append((f"[`{group}`]({group}/index.md)", info.get("summary", "")))
    lines.append(table(["Namespace", "Description"], rows))
    lines += ["## Methods on built-in types", ""]
    rows = []
    for kind in sorted(kinds):
        rows.append((f"[`{kind}`]({kind}/index.md)", TYPE_TITLES.get(kind, kind)))
    lines.append(table(["Type", "Methods"], rows))
    lines += ["## Importing", "",
              "A namespace is available once it is imported, except the globals, which "
              "are always there:", "",
              code_block(['import "std:json";', "", 'println(json.parse("[1]")[0]);']),
              "The import name is `std:` followed by the namespace. The compiled back end "
              "embeds the same table, so a script that imports `std:gl` on Windows compiles "
              "and reports the module as unavailable rather than failing to build.", "",
              "## Errors", "",
              "Every page has an **Errors** table naming the kind of failure and when it "
              "happens. The kind is what an uncaught error prints before its message "
              "(`NetError: connection refused`). A `catch` binds the message string itself, "
              "so the kind is documentation and diagnostics rather than something to test "
              "for:", "",
              code_block(['try {', '    net.connect("127.0.0.1", 1, {"timeout": 10});',
                          '} catch (e) {', '    println("failed:", e);', '}']),
              "The same kinds are raised by both back ends; the interpreter and the compiled "
              "binary report the same message for the same failure, which "
              "`tools/compare.sh` checks.", ""]
    files["README.md"] = "\n".join(lines) + "\n"

    return files, groups, kinds, covered


def main():
    files, groups, kinds, covered = render()
    missing = sorted(k for k in covered if k not in FUNCTIONS and k not in METHODS)
    extra = sorted(k for k in list(FUNCTIONS) + list(METHODS) if k not in covered)

    if "--check" in sys.argv:
        stale = []
        for relative, text in sorted(files.items()):
            path = os.path.join(OUT, relative)
            if not os.path.exists(path) or open(path, encoding="utf-8").read() != text:
                stale.append(relative)
        if stale:
            print(f"reference: {len(stale)} page(s) out of date, run tools/gen-docs.sh", file=sys.stderr)
            for relative in stale[:10]:
                print(f"  {relative}", file=sys.stderr)
            return 1
        print(f"reference: {len(files)} pages up to date")
        return 0

    for relative, text in files.items():
        path = os.path.join(OUT, relative)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)

    # Remove pages that no longer have a catalogue entry.
    for directory, _subdirs, names in os.walk(OUT):
        for name in names:
            if not name.endswith(".md"):
                continue
            path = os.path.join(directory, name)
            relative = os.path.relpath(path, OUT).replace(os.sep, "/")
            if relative not in files:
                os.unlink(path)

    print(f"reference: wrote {len(files)} pages to docs/msdn/")
    if missing:
        print(f"reference: {len(missing)} entr(ies) documented from the engine only:")
        for key in missing:
            print(f"  {key}")
    if extra:
        print(f"reference: {len(extra)} annotation(s) describe nothing in the catalogue:")
        for key in extra:
            print(f"  {key}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
