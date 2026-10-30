"""The client half of the Language Server Protocol.

The syntax server runs as a child process and talks `Content-Length`-framed
JSON over its standard streams, exactly as it would with any other editor. Qt's
event loop drives it: bytes arrive on a readyRead signal, complete messages are
paired with the request that asked for them, and the answer comes back through
a callback rather than a blocking wait, so a slow analysis can never freeze the
window.

Requests are not queued or coalesced. The server answers in microseconds for a
script-sized file, and delaying a reply to tidy up would only make the editor
feel less immediate.
"""

import json
import os

from PySide6.QtCore import QObject, QProcess, QProcessEnvironment, Signal


class LanguageServer(QObject):
    """Owns the server process and the request/response correlation."""

    diagnostics = Signal(str, list)      # uri, [{line, character, severity, message, code}]
    ready = Signal(bool)                 # True once initialize has been answered
    log = Signal(str)

    def __init__(self, command, cwd, parent=None):
        super().__init__(parent)
        self._command = command
        self._cwd = cwd
        self._buffer = b""
        self._next_id = 1
        self._pending = {}
        self._versions = {}
        self._documents = {}
        self._started = False

        self.process = QProcess(self)
        self.process.setProcessChannelMode(QProcess.SeparateChannels)
        self.process.readyReadStandardOutput.connect(self._read)
        self.process.readyReadStandardError.connect(self._read_error)
        self.process.finished.connect(self._finished)

    # ------------------------------------------------------------- lifecycle

    def start(self):
        """Launch the server and initialize the protocol."""
        program, *arguments = self._command
        self.log.emit(f"starting {program} {' '.join(arguments)}")
        # Set the directory rather than passing it to start(): PySide's overload
        # resolution is happier with the two-argument form.
        self.process.setWorkingDirectory(self._cwd)

        # The server runs on the bundled runtime, which needs DOTNET_ROOT the
        # way tools/tern sets it. Inheriting the shell's environment is not
        # enough when the IDE is started from a desktop launcher.
        environment = QProcessEnvironment.systemEnvironment()
        if not environment.contains("DOTNET_ROOT"):
            import os as _os
            root = _os.environ.get("TERN_TOOLCHAIN_DIR", "/tmp/tern-toolchain")
            environment.insert("DOTNET_ROOT", _os.path.join(root, "dotnet"))
        self.process.setProcessEnvironment(environment)
        self.process.start(program, arguments)

        if not self.process.waitForStarted(8000):
            self.log.emit("the syntax server did not start")
            self.ready.emit(False)
            return

        self._started = True
        self.request("initialize", {
            "processId": os.getpid(),
            "rootUri": "file://" + self._cwd,
            "capabilities": {
                "textDocument": {
                    "completion": {"completionItem": {"snippetSupport": False}},
                    "hover": {"contentFormat": ["markdown", "plaintext"]},
                    "synchronization": {"didSave": True},
                },
            },
        }, self._initialized)

    def _initialized(self, result):
        if result is None:
            self.ready.emit(False)
            return
        self.notify("initialized", {})
        self.ready.emit(True)

    def stop(self):
        """Asks the server to shut down, then makes sure it did.

        The polite sequence is the protocol's: a `shutdown` request answered,
        then an `exit` notification. Writes are buffered, so the bytes are
        flushed before waiting; if the server is still there — a wedged
        analysis, a runtime that never exits — closing its input ends it, and a
        kill ends it after that. A process left running would outlive the IDE
        and keep a language service for a window that no longer exists.
        """
        if not self._started:
            return

        try:
            self.request("shutdown", None)
            self.notify("exit", {})
            self.process.waitForBytesWritten(500)
        except Exception:
            pass

        if not self.process.waitForFinished(1200):
            self.process.closeWriteChannel()
            if not self.process.waitForFinished(800):
                self.process.kill()
                self.process.waitForFinished(500)

        self._started = False

    def _finished(self, code, status):
        self.log.emit(f"syntax server exited ({code})")
        self.ready.emit(False)

    # ------------------------------------------------------------- transport

    def _write(self, message):
        if not self._started or self.process.state() != QProcess.Running:
            return
        body = json.dumps(message).encode("utf-8")
        self.process.write(b"Content-Length: %d\r\n\r\n" % len(body) + body)

    def notify(self, method, params):
        self._write({"jsonrpc": "2.0", "method": method, "params": params})

    def request(self, method, params, callback):
        identifier = self._next_id
        self._next_id += 1
        self._pending[identifier] = (method, callback)
        self._write({"jsonrpc": "2.0", "id": identifier, "method": method, "params": params})

    def _read(self):
        self._buffer += bytes(self.process.readAllStandardOutput())
        while True:
            split = self._buffer.find(b"\r\n\r\n")
            if split < 0:
                return

            header = self._buffer[:split].decode("ascii", "replace")
            length = 0
            for line in header.split("\r\n"):
                if line.lower().startswith("content-length:"):
                    length = int(line.split(":", 1)[1].strip())

            start = split + 4
            if len(self._buffer) < start + length:
                return

            body = self._buffer[start:start + length]
            self._buffer = self._buffer[start + length:]

            try:
                message = json.loads(body)
            except json.JSONDecodeError:
                self.log.emit("unreadable message from the server")
                continue

            self._dispatch(message)

    def _dispatch(self, message):
        identifier = message.get("id")
        if identifier is not None and "method" not in message:
            entry = self._pending.pop(identifier, None)
            if entry is None:
                return
            method, callback = entry
            if "error" in message:
                self.log.emit(f"{method}: {message['error'].get('message')}")
                callback(None)
            else:
                callback(message.get("result"))
            return

        method = message.get("method")
        params = message.get("params") or {}
        if method == "textDocument/publishDiagnostics":
            self.diagnostics.emit(params.get("uri", ""), params.get("diagnostics", []))

    def _read_error(self):
        text = bytes(self.process.readAllStandardError()).decode("utf-8", "replace").strip()
        if text:
            self.log.emit(text)

    # -------------------------------------------------------------- documents

    def did_open(self, uri, text, version=1):
        self._documents[uri] = text
        self._versions[uri] = version
        self.notify("textDocument/didOpen", {
            "textDocument": {"uri": uri, "languageId": "tern", "version": version, "text": text},
        })
        self.analyze(uri)

    def did_change(self, uri, text, version=None):
        if uri not in self._documents:
            self.did_open(uri, text)
            return
        version = (self._versions.get(uri, 1) + 1) if version is None else version
        self._documents[uri] = text
        self._versions[uri] = version
        self.notify("textDocument/didChange", {
            "textDocument": {"uri": uri, "version": version},
            "contentChanges": [{"text": text}],
        })
        self.analyze(uri)

    def did_close(self, uri):
        self._documents.pop(uri, None)
        self._versions.pop(uri, None)
        self.notify("textDocument/didClose", {"textDocument": {"uri": uri}})

    def document(self, uri):
        return self._documents.get(uri)

    # --------------------------------------------------------------- features

    def analyze(self, uri):
        """Ask for diagnostics. The server pushes them through its own handler."""
        self.request("textDocument/diagnostic", {"textDocument": {"uri": uri}},
                     lambda result: self.diagnostics.emit(uri, result or []))

    def complete(self, uri, line, character, callback):
        self.request("textDocument/completion", {
            "textDocument": {"uri": uri},
            "position": {"line": line, "character": character},
        }, callback)

    def hover(self, uri, line, character, callback):
        self.request("textDocument/hover", {
            "textDocument": {"uri": uri},
            "position": {"line": line, "character": character},
        }, callback)

    def definition(self, uri, line, character, callback):
        self.request("textDocument/definition", {
            "textDocument": {"uri": uri},
            "position": {"line": line, "character": character},
        }, callback)

    def references(self, uri, line, character, callback, include_declaration=True):
        self.request("textDocument/references", {
            "textDocument": {"uri": uri},
            "position": {"line": line, "character": character},
            "context": {"includeDeclaration": include_declaration},
        }, callback)

    def rename(self, uri, line, character, new_name, callback):
        self.request("textDocument/rename", {
            "textDocument": {"uri": uri},
            "position": {"line": line, "character": character},
            "newName": new_name,
        }, callback)

    def document_symbols(self, uri, callback):
        self.request("textDocument/documentSymbol", {"textDocument": {"uri": uri}}, callback)

    def workspace_symbols(self, query, callback):
        self.request("workspace/symbol", {"query": query}, callback)

    def format_document(self, uri, callback):
        self.request("textDocument/formatting", {
            "textDocument": {"uri": uri},
            "options": {"tabSize": 4, "insertSpaces": True},
        }, callback)


def uri_for(path):
    return "file://" + os.path.abspath(path).replace("\\", "/")


def path_for(uri):
    if uri.startswith("file://"):
        return uri[len("file://"):]
    return uri
