#!/usr/bin/env python3
"""A structural check for the HLSL the examples generate.

This is not a compiler and does not pretend to be one. HLSL is compiled by
d3dcompiler_47.dll on Windows, which is not available in this sandbox, so a
shader that is generated here has never been through a real parser. What this
does is catch the mistakes that a text generator actually makes:

  * a delimiter left open - one missing "}" from a list of lines turns the rest
    of the shader into a syntax error at the first line of the next function,
    which is a confusing message from a real compiler;
  * a call to something that is neither defined in the file nor an HLSL
    intrinsic - the usual symptom of a typo in a hand-built line list, and
    something the C-style "undefined identifier" check would catch at compile
    time but not until someone runs it on Windows;
  * a `float` literal written without a decimal point, which HLSL accepts for
    whole numbers and then decides is an int when it is used in overload
    resolution - `max(x, 0)` with an int 0 is the classic one;
  * recursion, direct or mutual, which shader model 5.0 forbids outright. This
    is the one that got through: a ray tracer that called itself once per
    reflection passed every other check and then failed on the GPU with
    `error X3500: 'trace': recursive functions not allowed in ps_5_0`.

    python3 tools/d3d12/hlsl_lint.py shader.hlsl [more.hlsl]

Exit status is 1 when something is wrong, and the message names the line.
"""

import re
import sys

# The subset of HLSL intrinsics the examples use, plus the swizzle helpers.
INTRINSICS = {
    "abs", "acos", "asin", "atan", "atan2", "ceil", "clamp", "cos", "cosh",
    "cross", "ddx", "ddy", "degrees", "determinant", "distance", "dot", "exp",
    "exp2", "faceforward", "floor", "fmod", "frac", "length", "lerp", "lit",
    "log", "log10", "log2", "mad", "max", "min", "modf", "mul", "normalize",
    "pow", "radians", "reflect", "refract", "round", "rsqrt", "saturate",
    "sign", "sin", "sincos", "sinh", "smoothstep", "sqrt", "step", "tan",
    "tanh", "transpose", "trunc", "asfloat", "asint", "asuint", "clip",
    "all", "any", "countbits", "firstbithigh", "firstbitlow", "reversebits",
    "float", "float2", "float3", "float4", "int", "int2", "int3", "int4",
    "uint", "uint2", "uint3", "uint4", "bool", "bool2", "bool3", "bool4",
    "float2x2", "float3x3", "float4x4", "isnan", "isinf",
    # Statements that look like calls.
    "if", "for", "while", "switch", "return", "sizeof",
}

FUNCTION = re.compile(
    r"^\s*(?:static\s+)?(?:const\s+)?"
    r"(?:float[234]?|int[234]?|uint[234]?|bool|void|half[234]?|double)"
    r"(?:[234]x[234])?\s+([A-Za-z_]\w*)\s*\(",
    re.M,
)

CALL = re.compile(r"\b([A-Za-z_]\w*)\s*\(")

# Whole-number floats used as arguments, e.g. `max(x, 0)` or `pow(x, 2)`.
INTEGER_TO_FLOAT = re.compile(r"\(\s*([^()]*),\s*(-?\d+)\s*\)")

STRING_OR_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/", re.S)


def strip_comments(text):
    """Comments out, with their newlines kept so line numbers stay honest."""
    def blank(match):
        return "\n" * match.group(0).count("\n")
    return STRING_OR_COMMENT.sub(blank, text)


def check(text, path):
    problems = []
    clean = strip_comments(text)

    # 1. Delimiters, with a running line count so the message points at where
    #    the unclosed one was opened rather than where the file ended.
    stack = []
    for number, line in enumerate(clean.split("\n"), start=1):
        for position, character in enumerate(line):
            if character in "({[":
                stack.append((character, number, position))
            elif character in ")}]":
                expected = {")": "(", "}": "{", "]": "["}[character]
                if not stack or stack[-1][0] != expected:
                    opened = stack[-1] if stack else (None, 0, 0)
                    problems.append(
                        f"{path}:{number}: '{character}' closes nothing "
                        f"(the last opener was '{opened[0]}' at line {opened[1]})")
                    return problems
                stack.pop()
    if stack:
        character, number, _ = stack[-1]
        problems.append(f"{path}:{number}: '{character}' is never closed")

    # 2. Every call is a function defined in the file, an intrinsic, or a
    #    struct constructor.
    defined = set(FUNCTION.findall(clean))
    defined |= set(re.findall(r"\bstruct\s+([A-Za-z_]\w*)", clean))
    for match in re.finditer(r"\bstruct\s+([A-Za-z_]\w*)\s*\{.*?\}", clean, re.S):
        defined.add(match.group(1))
    keywords = {"if", "for", "while", "switch", "return", "struct", "static",
                "const", "else"}
    for number, line in enumerate(clean.split("\n"), start=1):
        if line.lstrip().startswith("//"):
            continue
        for call in CALL.finditer(line):
            name = call.group(1)
            if name in keywords or name in INTRINSICS or name in defined:
                continue
            problems.append(f"{path}:{number}: '{name}()' is not defined in this file "
                            f"and is not an HLSL intrinsic")

    # 3. Recursion. HLSL 5.0 has no call stack, so fxc rejects a cycle in the
    #    call graph - and a cycle is invisible in a shader that otherwise reads
    #    correctly, which is why this check exists rather than a comment.
    problems += recursive_functions(clean, path)

    # 4. Whole-number arguments where a float is meant. HLSL would compile
    #    `max(x, 0)` and pick the int overload, so this is a warning-shaped
    #    check: it is reported with the rest because the examples are meant to
    #    be read as HLSL, and `0` in a shader about floats reads like a mistake.
    for number, line in enumerate(clean.split("\n"), start=1):
        for call in re.finditer(r"\b(max|min|pow|clamp|lerp|smoothstep|step)\s*\(", line):
            start = call.end()
            depth = 1
            end = start
            while end < len(line) and depth > 0:
                if line[end] == "(":
                    depth += 1
                elif line[end] == ")":
                    depth -= 1
                end += 1
            arguments = line[start:end - 1]
            for argument in arguments.split(","):
                argument = argument.strip()
                if re.fullmatch(r"-?\d+", argument):
                    problems.append(
                        f"{path}:{number}: '{call.group(1)}' is given the integer "
                        f"'{argument}'; HLSL will pick the int overload - write "
                        f"'{argument}.0'")

    return problems


def function_bodies(clean):
    """Every function defined in the file, mapped to the body between its braces."""
    bodies = {}
    for match in FUNCTION.finditer(clean):
        name = match.group(1)
        start = clean.find("{", match.end())
        if start < 0:
            continue
        depth = 0
        end = start
        while end < len(clean):
            if clean[end] == "{":
                depth += 1
            elif clean[end] == "}":
                depth -= 1
                if depth == 0:
                    break
            end += 1
        bodies[name] = clean[start + 1:end]
    return bodies


def call_graph(clean):
    """Which defined functions each defined function calls."""
    bodies = function_bodies(clean)
    graph = {}
    for name, body in bodies.items():
        graph[name] = {call.group(1) for call in CALL.finditer(body)} & set(bodies)
    return graph, bodies


def recursive_functions(clean, path):
    """Every cycle in the call graph, as a path of names."""
    graph, bodies = call_graph(clean)
    problems = []
    seen_cycles = set()

    def walk(name, trail):
        for callee in sorted(graph.get(name, ())):
            if callee in trail:
                cycle = trail[trail.index(callee):] + [callee] if callee != name else [name, name]
                key = frozenset(cycle)
                if key in seen_cycles:
                    continue
                seen_cycles.add(key)
                line = 0
                for number, text in enumerate(clean.split("\n"), start=1):
                    if re.search(r"\b" + re.escape(cycle[0]) + r"\s*\(", text) and \
                            re.match(r"\s*(?:static\s+)?(?:const\s+)?[A-Za-z_]", text) and \
                            cycle[0] in text.split("(")[0]:
                        line = number
                        break
                problems.append(
                    f"{path}:{line}: '{cycle[0]}' is recursive ({' -> '.join(cycle)}); "
                    "shader model 5.0 has no call stack, so fxc rejects it with "
                    "'error X3500: recursive functions not allowed'. Rewrite it as a "
                    "loop that carries the state forward")
                continue
            walk(callee, trail + [callee])

    for name in sorted(graph):
        walk(name, [name])
    return problems


def main(argv):
    if not argv:
        print("usage: hlsl_lint.py FILE.hlsl [FILE.hlsl...]")
        return 2
    failed = 0
    for path in argv:
        try:
            text = open(path, encoding="utf-8").read()
        except OSError as error:
            print(f"{path}: {error}")
            failed = 1
            continue
        problems = check(text, path)
        if problems:
            failed = 1
            for problem in problems:
                print(problem)
        else:
            print(f"{path}: ok ({len(text.splitlines())} lines)")
    return failed


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
