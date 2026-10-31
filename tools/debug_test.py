#!/usr/bin/env python3
"""Tests for the TERN debugger, driven over its protocol.

Each case writes a small program, starts the debugger on it, and asserts on the
events: where it stopped, what the stack said, what the locals were, and how it
finished. Nothing here needs a display.

    python3 tools/debug_test.py [-v]
"""

import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)

from tools.ide.debug_client import DebugClient  # noqa: E402


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
            print(f"  FAIL {name}" + (f" — {detail}" if detail else ""))


def write(directory, name, text):
    path = os.path.join(directory, name)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)
    return path


PROGRAM = """fn add(a, b) {
    let total = a + b;
    return total;
}

fn main() {
    let x = 1;
    let y = 2;
    let sum = add(x, y);
    println("sum is ${sum}");
}

main();
"""


def main():
    verbose = "-v" in sys.argv or "--verbose" in sys.argv
    checks = Checks(verbose)
    directory = tempfile.mkdtemp(prefix="tern-debug-")

    print("TERN debugger: protocol suite")

    # ------------------------------------------------------------ breakpoint
    program = write(directory, "add.tern", PROGRAM)

    client = DebugClient()
    response = client.launch(program, breakpoints=[2])
    checks.check("launch is acknowledged", bool(response and response.get("ok")), str(response))
    checks.check("the program starts", client.wait_for_event("started") is not None)

    stopped = client.wait_for_event("stopped")
    checks.check("a breakpoint stops the program", stopped is not None, str(stopped))
    checks.check("the stop is on the breakpoint's line", (stopped or {}).get("line") == 2,
                 str((stopped or {}).get("line")))
    checks.check("the stop reason is the breakpoint", (stopped or {}).get("reason") == "breakpoint",
                 str((stopped or {}).get("reason")))

    frames = (stopped or {}).get("frames", [])
    names = [frame.get("name") for frame in frames]
    checks.check("the stack names the function and its callers",
                 names[:3] == ["add", "main", "<module>"], str(names))
    checks.check("the caller's line is reported", frames[1].get("line") == 9 if len(frames) > 1 else False,
                 str(frames[1] if len(frames) > 1 else None))

    variables = client.request("variables").get("variables") or []
    by_name = {variable["name"]: variable for variable in variables}
    checks.check("the parameters are in scope", by_name.get("a", {}).get("value") == "1"
                 and by_name.get("b", {}).get("value") == "2", str(by_name))
    checks.check("a local that has not run yet is nil",
                 by_name.get("total", {}).get("value") == "nil", str(by_name.get("total")))
    checks.check("values carry their type", by_name.get("a", {}).get("type") == "int",
                 str(by_name.get("a")))

    # --------------------------------------------------------------- stepping
    client.request("next")
    stopped = client.wait_for_event("stopped")
    checks.check("Next moves to the following line", (stopped or {}).get("line") == 3,
                 str((stopped or {}).get("line")))
    checks.check("the step's reason is the step", (stopped or {}).get("reason") == "step",
                 str((stopped or {}).get("reason")))

    variables = client.request("variables").get("variables") or []
    by_name = {variable["name"]: variable for variable in variables}
    checks.check("the assigned local is now visible", by_name.get("total", {}).get("value") == "3",
                 str(by_name.get("total")))
    checks.check("evaluate reads a local",
                 client.request("evaluate", expression="total").get("value") == "3",
                 str(client.request("evaluate", expression="total")))

    # The call and the rest of its statement are one line, so returning from
    # `add` lands the module frame back on line 9.
    client.request("stepOut")
    stopped = client.wait_for_event("stopped")
    checks.check("Step out returns to the caller", (stopped or {}).get("line") == 9,
                 str((stopped or {}).get("line")))
    checks.check("the stack is back to two frames",
                 len((stopped or {}).get("frames", [])) == 2,
                 str((stopped or {}).get("frames")))

    # ------------------------------------------------------- run to the end
    client.request("continue")
    output = []
    while True:
        message = client.read()
        if message is None:
            break
        if message.get("type") == "event" and message.get("event") == "output":
            output.append(message.get("text", ""))
        if message.get("type") == "event" and message.get("event") == "exited":
            checks.check("the program exits cleanly", message.get("ok") is True, str(message))
            break

    checks.check("the program's output is forwarded", "".join(output).strip() == "sum is 3",
                 repr("".join(output)))
    checks.check("the debugger shuts down when its input closes", client.close() == 0)

    # ------------------------------------------------------------- step in
    client = DebugClient()
    client.launch(program, breakpoints=[9])
    stopped = client.wait_for_event("stopped")
    checks.check("the module breakpoint is hit", (stopped or {}).get("line") == 9,
                 str((stopped or {}).get("line")))

    client.request("stepIn")
    stopped = client.wait_for_event("stopped")
    checks.check("Step in enters the called function", (stopped or {}).get("line") == 2,
                 str((stopped or {}).get("line")))
    frames = (stopped or {}).get("frames", [])
    checks.check("the stack grew by one", len(frames) == 3, str(len(frames)))
    client.request("terminate")
    client.close()

    # ---------------------------------------------------------- break in a loop
    loop_program = write(directory, "loop.tern", """let total = 0;
for n in 1..6 {
    total = total + n;
}
println("total is ${total}");
""")

    client = DebugClient()
    client.launch(loop_program, breakpoints=[3])
    hits = []
    stopped = client.wait_for_event("stopped")
    hits.append((stopped or {}).get("line"))
    variables = client.request("variables").get("variables") or []
    by_name = {variable["name"]: variable for variable in variables}
    checks.check("the loop variable is in scope", "total" in by_name, str(list(by_name)))
    first_total = by_name.get("total", {}).get("value")

    client.request("continue")
    stopped = client.wait_for_event("stopped")
    hits.append((stopped or {}).get("line"))
    variables = client.request("variables").get("variables") or []
    by_name = {variable["name"]: variable for variable in variables}
    second_total = by_name.get("total", {}).get("value")

    checks.check("a breakpoint inside a loop fires again", hits == [3, 3], str(hits))
    checks.check("the loop's state advanced between hits", first_total != second_total,
                 f"{first_total} then {second_total}")

    # Dropping the breakpoint while stopped lets it run out.
    client.request("breakpoints", breakpoints=[])
    client.request("continue")
    exited = client.wait_for_event("exited")
    checks.check("the loop program finishes", exited is not None and exited.get("ok") is True,
                 str(exited))
    checks.check("the looping program shuts the debugger down", client.close() == 0)

    # ------------------------------------------------- conditional breakpoints
    # `total == 6` is true on exactly one iteration of 1..6, so it stops once
    # and then runs on to the end.
    client = DebugClient()
    client.launch(loop_program, breakpoints=[{"line": 3, "condition": "total == 6"}])
    stopped = client.wait_for_event("stopped")
    variables = client.request("variables").get("variables") or []
    by_name = {variable["name"]: variable for variable in variables}
    checks.check("a conditional breakpoint stops when its condition holds",
                 stopped is not None and stopped.get("line") == 3, str(stopped))
    checks.check("it stopped on the right iteration", by_name.get("total", {}).get("value") == "6",
                 str(by_name.get("total")))

    client.request("continue")
    exited = client.wait_for_event("exited")
    checks.check("a condition that goes false stops firing",
                 exited is not None and exited.get("ok") is True, str(exited))
    client.close()

    # A condition that is never true runs straight past the line.
    client = DebugClient()
    client.launch(loop_program, breakpoints=[{"line": 3, "condition": "total > 100"}])
    exited = client.wait_for_event("exited")
    checks.check("a false condition runs straight past",
                 exited is not None and exited.get("ok") is True, str(exited))
    client.close()

    # Conditions compare against strings and other variables too.
    string_program = write(directory, "names.tern", """let name = "warn";
for n in 1..4 {
    name = n == 2 ? "error" : "warn";
}
println(name);
""")
    client = DebugClient()
    client.launch(string_program, breakpoints=[{"line": 3, "condition": 'name == "error"'}])
    stopped = client.wait_for_event("stopped")
    checks.check("a string condition matches", stopped is not None and stopped.get("line") == 3,
                 str(stopped))
    client.request("terminate")
    client.close()

    # A condition that cannot be evaluated must not hide the stop.
    client = DebugClient()
    client.launch(loop_program, breakpoints=[{"line": 3, "condition": "no_such_name"}])
    stopped = client.wait_for_event("stopped")
    checks.check("a broken condition still stops", stopped is not None and stopped.get("line") == 3,
                 str(stopped))
    client.request("terminate")
    client.close()

    # ------------------------------------------------------------ no stops
    client = DebugClient()
    client.launch(program)
    output = []
    exited = None
    while True:
        message = client.read()
        if message is None:
            break
        if message.get("type") == "event" and message.get("event") == "output":
            output.append(message.get("text", ""))
        if message.get("type") == "event" and message.get("event") == "exited":
            exited = message
            break
    checks.check("a program with no breakpoints runs to completion",
                 exited is not None and exited.get("ok") is True, str(exited))
    checks.check("it prints what it should", "".join(output).strip() == "sum is 3",
                 repr("".join(output)))
    client.close()

    # ------------------------------------------------------- runtime errors
    broken = write(directory, "broken.tern", """fn boom() {
    throw "on purpose";
}

boom();
""")
    client = DebugClient()
    client.launch(broken, breakpoints=[2])
    client.wait_for_event("stopped")
    client.request("continue")
    exited = client.wait_for_event("exited")
    checks.check("a thrown error ends the program", exited is not None and exited.get("ok") is False,
                 str(exited))
    checks.check("the error type comes through", (exited or {}).get("type") is not None,
                 str(exited))
    checks.check("the error message comes through",
                 "on purpose" in str((exited or {}).get("message")), str(exited))
    client.close()

    # ------------------------------------------------- breakpoints set late
    # A breakpoint added while the program is stopped takes effect the next time
    # its function runs: the entry hook installs it.
    late = write(directory, "late.tern", """fn tick(n) {
    return n * 2;
}

let total = 0;
for n in 1..4 {
    total = total + tick(n);
}
println(total);
""")

    client = DebugClient()
    client.launch(late, breakpoints=[7])
    stopped = client.wait_for_event("stopped")
    checks.check("the first stop is on the loop line", (stopped or {}).get("line") == 7,
                 str((stopped or {}).get("line")))

    client.request("breakpoints", breakpoints=[{"line": 2}])
    client.request("continue")
    stopped = client.wait_for_event("stopped")
    checks.check("a breakpoint added while stopped still stops",
                 (stopped or {}).get("line") == 2, str((stopped or {}).get("line")))

    client.request("breakpoints", breakpoints=[])
    client.request("continue")
    exited = client.wait_for_event("exited")
    checks.check("the late-breakpoint program finishes",
                 exited is not None and exited.get("ok") is True, str(exited))
    client.close()

    print()
    print(f"passed: {checks.passed}  failed: {len(checks.failed)}")
    for name in checks.failed:
        print(f"  failed: {name}")
    return 1 if checks.failed else 0


if __name__ == "__main__":
    sys.exit(main())
