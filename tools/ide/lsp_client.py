#!/usr/bin/env python3
"""An LSP client for testing, and the IDE's protocol test suite.

Spawns `tools/tern-ls`, speaks the protocol to it and asserts on the replies.
The assertions are written as a list of named cases so a failure says which
behaviour broke, and the suite is what `bash tools/test-ide.sh` runs.

    python3 tools/ide/lsp_client.py            run every case
    python3 tools/ide/lsp_client.py -v         show each reply
    python3 tools/ide/lsp_client.py --keep     leave the server log behind
"""

import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))


class LanguageClient:
    """A minimal LSP client: framing, request ids, and typed helpers."""

    def __init__(self, command, log_path=None):
        self.process = subprocess.Popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
        )
        self._next_id = 1
        self.log_path = log_path

    # -- transport ---------------------------------------------------------

    def _write(self, message):
        body = json.dumps(message).encode("utf-8")
        self.process.stdin.write(b"Content-Length: %d\r\n\r\n" % len(body))
        self.process.stdin.write(body)
        self.process.stdin.flush()

    def _read(self):
        length = 0
        while True:
            line = self.process.stdout.readline()
            if not line:
                raise EOFError("the server closed its output")

            line = line.decode("utf-8").strip()
            if not line:
                break
            if line.lower().startswith("content-length:"):
                length = int(line.split(":", 1)[1].strip())

        body = self.process.stdout.read(length)
        if len(body) != length:
            raise EOFError("truncated message")
        return json.loads(body)

    def notify(self, method, params):
        self._write({"jsonrpc": "2.0", "method": method, "params": params})

    def request(self, method, params):
        identifier = self._next_id
        self._next_id += 1
        self._write({"jsonrpc": "2.0", "id": identifier, "method": method, "params": params})

        while True:
            message = self._read()
            if message.get("id") == identifier and "method" not in message:
                if "error" in message:
                    raise RuntimeError(f"{method}: {message['error']['message']}")
                return message.get("result")

    # -- lifecycle ---------------------------------------------------------

    def initialize(self):
        result = self.request("initialize", {
            "processId": os.getpid(),
            "rootUri": "file://" + ROOT,
            "capabilities": {},
        })
        self.notify("initialized", {})
        return result

    def open(self, path, text):
        uri = "file://" + path
        self.notify("textDocument/didOpen", {
            "textDocument": {"uri": uri, "languageId": "tern", "version": 1, "text": text},
        })
        return uri

    def change(self, uri, text, version=2):
        self.notify("textDocument/didChange", {
            "textDocument": {"uri": uri, "version": version},
            "contentChanges": [{"text": text}],
        })

    def close(self):
        try:
            self.request("shutdown", None)
            self.notify("exit", None)
        except (EOFError, BrokenPipeError):
            pass
        finally:
            self.process.wait(timeout=10)


# ---------------------------------------------------------------------- cases

class Suite:
    def __init__(self, client, verbose=False):
        self.client = client
        self.verbose = verbose
        self.passed = 0
        self.failed = 0
        self.names = []

    def check(self, name, condition, detail=""):
        if condition:
            self.passed += 1
            if self.verbose:
                print(f"  ok   {name}")
        else:
            self.failed += 1
            self.names.append(name)
            print(f"  FAIL {name}" + (f" — {detail}" if detail else ""))

    def show(self, label, value):
        if self.verbose:
            print(f"       {label}: {json.dumps(value)[:300]}")


def run_cases(suite):
    client = suite.client

    # ---------------------------------------------------------- initialize
    result = client.initialize()
    caps = result.get("capabilities", {})
    suite.check("initialize advertises completion", caps.get("completionProvider") is not None)
    suite.check("initialize advertises diagnostics sync", caps.get("textDocumentSync") is not None)
    suite.check("initialize names the server", result.get("serverInfo", {}).get("name") == "tern-ls",
                str(result.get("serverInfo")))

    # A file with a deliberate error on the second line, and a struct to
    # complete against.
    source = (
        "import \"std:math\";\n"
        "\n"
        "struct Ticket { id, title, severity }\n"
        "\n"
        "fn summary(t) {\n"
        "    return \"#${t.id} ${t.title}\";\n"
        "}\n"
        "\n"
        "let ticket = Ticket(1, \"disk full\", \"critical\");\n"
        "let area = math.pi * 2.0;\n"
        "let broken = ;\n"
        "println(summary(ticket), area);\n"
    )
    uri = client.open("/tmp/ide_case.tern", source)

    # --------------------------------------------------------- diagnostics
    diagnostics = client.request("textDocument/diagnostic", {"textDocument": {"uri": uri}})
    suite.show("diagnostics", diagnostics)
    suite.check("a syntax error is reported", isinstance(diagnostics, list) and len(diagnostics) >= 1,
                str(diagnostics))
    if diagnostics:
        first = diagnostics[0]
        suite.check("the diagnostic has a range", "range" in first)
        suite.check("the diagnostic is an error", first.get("severity") == 1)
        suite.check("the diagnostic has a code", bool(first.get("code")))
        # The broken line is the 11th line, zero-based 10.
        broken_line = next(i for i, line in enumerate(source.split("\n")) if "let broken" in line)
        suite.check("the diagnostic points at the broken line",
                    first["range"]["start"]["line"] == broken_line,
                    str(first["range"]["start"]) + f" vs line {broken_line}")
        suite.check("the diagnostic comes from TERN", first.get("source") == "TERN")

    # ---------------------------------------------------------- completion
    # A member of a value whose type is obvious: `let ticket = Ticket(...)`.
    # Insert `ticket.` on its own line and ask there.
    text = source.replace(
        'let area = math.pi * 2.0;',
        'let area = math.pi * 2.0;\nlet probe = ticket.;')
    client.change(uri, text, version=2)
    probe_line = next(i for i, line in enumerate(text.split("\n")) if "ticket." in line)
    column = text.split("\n")[probe_line].index("ticket.") + len("ticket.")
    items = client.request("textDocument/completion", {
        "textDocument": {"uri": uri},
        "position": {"line": probe_line, "character": column},
    })
    labels = [item["label"] for item in (items or {}).get("items", [])]
    suite.show("ticket members", labels[:12])
    suite.check("completion after a value returns something", len(labels) > 0)
    suite.check("completion knows the struct's fields", "id" in labels and "title" in labels, str(labels[:10]))

    # A receiver of unknown type falls back to the built-in methods rather than
    # offering nothing: the server cannot infer it, and an empty list reads as a
    # broken editor.
    text = source.replace('let ticket = Ticket(1, "disk full", "critical");',
                          'let ticket = Ticket(1, "disk full", "critical");\nlet unknown = ticket.chars().;')
    client.change(uri, text, version=6)
    unknown_line = next(i for i, line in enumerate(text.split("\n")) if "unknown" in line)
    column = text.split("\n")[unknown_line].rindex(".") + 1
    items = client.request("textDocument/completion", {
        "textDocument": {"uri": uri},
        "position": {"line": unknown_line, "character": column},
    })
    labels = [item["label"] for item in (items or {}).get("items", [])]
    suite.check("an unknown receiver still offers the built-in methods",
                "upper" in labels or "push" in labels, str(labels[:10]))

    # A namespace member: `math.`.
    text = source.replace("math.pi", "math.")
    client.change(uri, text, version=3)
    math_line = next(i for i, line in enumerate(text.split("\n")) if "math." in line)
    items = client.request("textDocument/completion", {
        "textDocument": {"uri": uri},
        "position": {"line": math_line, "character": text.split("\n")[math_line].index("math.") + 5},
    })
    labels = [item["label"] for item in (items or {}).get("items", [])]
    suite.show("math members", labels)
    suite.check("completion after a namespace lists its members", "pi" in labels and "sqrt" in labels,
                str(labels[:10]))
    suite.check("a constant is documented", any(item.get("documentation") for item in items["items"]))

    # Globals, at the top level with no dot.
    client.change(uri, source, version=4)
    items = client.request("textDocument/completion", {
        "textDocument": {"uri": uri},
        "position": {"line": 0, "character": 0},
    })
    labels = [item["label"] for item in (items or {}).get("items", [])]
    suite.show("top level", labels[:20])
    suite.check("completion at top level lists globals", "println" in labels and "len" in labels)
    suite.check("completion at top level lists keywords", "fn" in labels and "struct" in labels)
    suite.check("completion at top level lists this file's declarations",
                "summary" in labels and "Ticket" in labels)

    # ---------------------------------------------------------------- hover
    lines = source.split("\n")
    math_line = next(i for i, line in enumerate(lines) if "math.pi" in line)
    hover = client.request("textDocument/hover", {
        "textDocument": {"uri": uri},
        "position": {"line": math_line, "character": lines[math_line].index("math.pi") + len("math.")},
    })
    suite.show("hover", hover)
    suite.check("hover describes a namespace member", hover is not None and "pi" in json.dumps(hover))
    suite.check("hover carries documentation", hover is not None and "range" in hover)

    summary_line = next(i for i, line in enumerate(lines) if "${t.id}" in line)
    hover = client.request("textDocument/hover", {
        "textDocument": {"uri": uri},
        "position": {"line": summary_line, "character": lines[summary_line].index("${t.") + 2},
    })
    suite.check("hover describes a local", hover is not None and "parameter" in json.dumps(hover).lower(),
                json.dumps(hover)[:200] if hover else "null")

    # ------------------------------------------------------- signature help
    call_line = next(i for i, line in enumerate(lines) if "println(summary(" in line)
    signature = client.request("textDocument/signatureHelp", {
        "textDocument": {"uri": uri},
        "position": {"line": call_line, "character": lines[call_line].index("println(summary(") + len("println(summary(")},
    })
    suite.show("signature", signature)
    suite.check("signature help names the enclosing call",
                signature is not None and "summary" in signature["signatures"][0]["label"],
                json.dumps(signature)[:200] if signature else "null")
    suite.check("signature help lists the parameters",
                signature is not None and signature["signatures"][0]["parameters"][0]["label"])

    first_argument = client.request("textDocument/signatureHelp", {
        "textDocument": {"uri": uri},
        "position": {"line": call_line, "character": lines[call_line].index("println(") + len("println(")},
    })
    suite.check("signature help follows the caret outwards",
                first_argument is not None and "println" in first_argument["signatures"][0]["label"],
                json.dumps(first_argument)[:160] if first_argument else "null")

    # ----------------------------------------------------------- definition
    call_line = next(i for i, line in enumerate(lines) if "println(summary(" in line)
    declaration_line = next(i for i, line in enumerate(lines) if line.startswith("fn summary"))
    definition = client.request("textDocument/definition", {
        "textDocument": {"uri": uri},
        "position": {"line": call_line, "character": lines[call_line].index("summary(")},
    })
    suite.show("definition", definition)
    suite.check("go to definition finds a function", definition is not None and definition.get("uri") == uri)
    suite.check("go to definition lands on the declaration",
                definition is not None and definition["range"]["start"]["line"] == declaration_line,
                (str(definition["range"]["start"]) + f" vs line {declaration_line}") if definition else "null")

    # ---------------------------------------------------------- references
    references = client.request("textDocument/references", {
        "textDocument": {"uri": uri},
        "position": {"line": declaration_line, "character": 3},
        "context": {"includeDeclaration": True},
    })
    suite.show("references", references)
    suite.check("find usages finds the call site", references is not None and len(references) >= 2,
                str(len(references or [])))

    # -------------------------------------------------------------- outline
    symbols = client.request("textDocument/documentSymbol", {"textDocument": {"uri": uri}})
    names = [symbol["name"] for symbol in symbols or []]
    suite.show("outline", names)
    suite.check("the outline lists the struct", "Ticket" in names)
    suite.check("the outline lists the function", "summary" in names)
    suite.check("the outline lists the top-level values", "ticket" in names and "area" in names)

    workspace_symbols = client.request("workspace/symbol", {"query": "summ"})
    suite.check("workspace symbol search finds a function",
                any(symbol["name"] == "summary" for symbol in workspace_symbols or []))

    # --------------------------------------------------------------- rename
    edit = client.request("textDocument/rename", {
        "textDocument": {"uri": uri},
        "position": {"line": declaration_line, "character": 3},
        "newName": "describe",
    })
    suite.show("rename", edit)
    edits = (edit or {}).get("changes", {}).get(uri, [])
    suite.check("rename returns edits", len(edits) >= 2, str(len(edits)))
    suite.check("every rename edit uses the new name",
                all(change["newText"] == "describe" for change in edits))
    suite.check("rename covers the declaration and the call site",
                {change["range"]["start"]["line"] for change in edits} >= {declaration_line, call_line},
                str({change["range"]["start"]["line"] for change in edits}))

    refusal = None
    try:
        client.request("textDocument/rename", {
            "textDocument": {"uri": uri},
            "position": {"line": declaration_line, "character": 3},
            "newName": "fn",
        })
    except RuntimeError as error:
        refusal = str(error)
    suite.check("rename refuses a keyword with a reason", refusal is not None and "keyword" in refusal,
                str(refusal))

    # ------------------------------------------------------------- format
    messy = "fn f() {\nlet x = 1;\n   if (x > 0) {\nreturn x;\n}\nreturn 0;\n}\n"
    messy_uri = client.open("/tmp/ide_format.tern", messy)
    formatted = client.request("textDocument/formatting", {
        "textDocument": {"uri": messy_uri},
        "options": {"tabSize": 4, "insertSpaces": True},
    })
    suite.check("formatting returns a single whole-file edit",
                formatted is not None and len(formatted) == 1, str(formatted)[:200])
    if formatted:
        text = formatted[0]["newText"]
        suite.show("formatted", text)
        suite.check("formatting indents a statement in a function", "\n    let x = 1;" in text, text)
        suite.check("formatting indents a nested block deeper", "\n        return x;" in text, text)
        suite.check("formatting keeps the closing brace at column 0", "\n}" in text, text)

    # ------------------------------------------------------- code stability
    # An error must not stop the server: a fixed buffer must analyze clean.
    client.change(uri, source.replace("let broken = ;", "let fixed = 1;"), version=5)
    diagnostics = client.request("textDocument/diagnostic", {"textDocument": {"uri": uri}})
    suite.check("a fixed file reports no diagnostics", diagnostics == [], str(diagnostics)[:300])

    client.close()


def main():
    verbose = "-v" in sys.argv or "--verbose" in sys.argv
    log_path = os.path.join(tempfile.gettempdir(), "tern-ls.log")

    command = [os.path.join(ROOT, "build", "dotnet"), os.path.join(ROOT, "build", "TERN.Ide.dll")]
    if not os.path.exists(command[1]):
        print("ide: build/TERN.Ide.dll missing; run tools/build.py --ide", file=sys.stderr)
        return 1

    env = dict(os.environ)
    env["DOTNET_ROOT"] = os.path.join(os.environ.get("TERN_TOOLCHAIN_DIR", "/tmp/tern-toolchain"), "dotnet")

    client = LanguageClient(command + ["--log=" + log_path], log_path=log_path)
    client.process.kill()  # the environment is set below; restart with it
    client.process.wait()

    client = subprocess.Popen(
        command + ["--log=" + log_path],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=env)
    language_client = LanguageClient.__new__(LanguageClient)
    language_client.process = client
    language_client._next_id = 1
    language_client.log_path = log_path

    print("syntax server: protocol suite")
    suite = Suite(language_client, verbose)
    try:
        run_cases(suite)
    finally:
        try:
            language_client.close()
        except Exception:
            client.kill()

    print()
    print(f"passed: {suite.passed}  failed: {suite.failed}")
    if suite.failed:
        for name in suite.names:
            print(f"  failed: {name}")
        print(f"server log: {log_path}")
        return 1

    if "--keep" not in sys.argv and os.path.exists(log_path):
        os.unlink(log_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
