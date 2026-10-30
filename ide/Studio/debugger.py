"""The IDE's half of the debugger: a child process and a Qt-shaped wrapper.

The debugger speaks one JSON object per line (see `ide/Debug/Program.cs`). This
adapter runs it, parses the lines on the GUI thread with a Qt socket notifier
doing the reading, and turns them into Qt signals, so the window never blocks.

The protocol is request/response with events in between. A request is parked
until its response arrives, because some requests (continue) are answered before
the events that follow them.
"""

import json
import os

from PySide6.QtCore import QObject, QProcess, QProcessEnvironment, Signal


class DebugSession(QObject):
    """One debugged program: start it, drive it, tear it down."""

    started = Signal()
    stopped = Signal(dict)          # {reason, line, frames}
    output = Signal(str)
    exited = Signal(bool, str)      # ok, message
    failed = Signal(str)            # the debugger itself could not run
    server_log = Signal(str)

    def __init__(self, program, breakpoints, root, parent=None):
        super().__init__(parent)
        self.program = os.path.abspath(program)
        self.breakpoints = list(breakpoints)
        self.root = root
        self.process = QProcess(self)
        self.process.setProcessChannelMode(QProcess.SeparateChannels)
        self.process.readyReadStandardOutput.connect(self._read)
        self.process.readyReadStandardError.connect(self._read_error)
        self.process.finished.connect(self._finished)
        self._buffer = b""
        self._next_id = 1
        self._callbacks = {}
        self._alive = False

    # ------------------------------------------------------------- lifecycle

    def _command(self):
        """How to start the debugger, preferring the installation's runtime."""
        roots = [self.root, os.path.dirname(os.path.dirname(
            os.path.dirname(os.path.abspath(__file__))))]
        for base in roots:
            launcher = os.path.join(base, "build", "dotnet")
            assembly = os.path.join(base, "build", "TERN.Debug.dll")
            if os.path.exists(launcher) and os.path.exists(assembly):
                return launcher, assembly, base

        toolchain = os.environ.get("TERN_TOOLCHAIN_DIR", "/tmp/tern-toolchain")
        candidate = os.path.join(toolchain, "dotnet", "dotnet")
        if os.path.exists(candidate):
            return candidate, os.path.join(self.root, "build", "TERN.Debug.dll"), self.root
        return "dotnet", os.path.join(self.root, "build", "TERN.Debug.dll"), self.root

    def start(self):
        launcher, assembly, base = self._command()
        if not os.path.exists(assembly):
            self.failed.emit(f"{assembly} is missing; run tools/build.py --debugger")
            return False

        environment = QProcessEnvironment.systemEnvironment()
        if not environment.contains("DOTNET_ROOT"):
            environment.insert("DOTNET_ROOT", os.path.join(
                os.environ.get("TERN_TOOLCHAIN_DIR", "/tmp/tern-toolchain"), "dotnet"))
        self.process.setProcessEnvironment(environment)
        self.process.setWorkingDirectory(base)
        self.process.start(launcher, [assembly])
        if not self.process.waitForStarted(5000):
            self.failed.emit(self.process.errorString() or "could not start the debugger")
            return False

        self._alive = True
        self.request("launch", program=self.program,
                     breakpoints=[{"line": line} for line in self.breakpoints])
        return True

    def stop(self):
        if self.process.state() != QProcess.NotRunning:
            try:
                self.request("terminate")
            except Exception:
                pass
            if not self.process.waitForFinished(800):
                self.process.kill()
                self.process.waitForFinished(500)
        self._alive = False

    @property
    def running(self):
        return self._alive and self.process.state() != QProcess.NotRunning

    # -------------------------------------------------------------- messages

    def send(self, command, callback=None, **arguments):
        """Sends a command. `callback` receives the response when it arrives."""
        identifier = self._next_id
        self._next_id += 1
        if callback is not None:
            self._callbacks[identifier] = callback

        message = {"id": identifier, "command": command, **arguments}
        if not self.running:
            return identifier
        self.process.write((json.dumps(message) + "\n").encode("utf-8"))
        return identifier

    def request(self, command, **arguments):
        return self.send(command, None, **arguments)

    def _read(self):
        self._buffer += bytes(self.process.readAllStandardOutput())
        while b"\n" in self._buffer:
            line, _, self._buffer = self._buffer.partition(b"\n")
            line = line.strip()
            if not line:
                continue
            try:
                message = json.loads(line.decode("utf-8", "replace"))
            except json.JSONDecodeError:
                self.server_log.emit(line.decode("utf-8", "replace"))
                continue
            self._dispatch(message)

    def _read_error(self):
        text = bytes(self.process.readAllStandardError()).decode("utf-8", "replace")
        for line in text.splitlines():
            if line.strip():
                self.server_log.emit(line.rstrip())

    def _dispatch(self, message):
        kind = message.get("type")

        if kind == "response":
            callback = self._callbacks.pop(message.get("id"), None)
            if callback is not None:
                callback(message)
            return

        if kind != "event":
            return

        event = message.get("event")
        if event == "started":
            self.started.emit()
        elif event == "stopped":
            self.stopped.emit(message)
        elif event == "output":
            self.output.emit(message.get("text", ""))
        elif event == "exited":
            self._alive = False
            # The debugger is still reading commands; end of input is what makes
            # it exit, and leaving it running would outlive the session.
            self.process.closeWriteChannel()
            self.exited.emit(bool(message.get("ok")), message.get("message", ""))

    def _finished(self, *_):
        was_alive = self._alive
        self._alive = False
        if was_alive:
            self.exited.emit(False, "the debugger stopped unexpectedly")

    # -------------------------------------------------------------- commands

    def resume(self):
        self.request("continue")

    def step_over(self):
        self.request("next")

    def step_into(self):
        self.request("stepIn")

    def step_out(self):
        self.request("stepOut")

    def variables(self, frame, callback):
        self.send("variables", callback, frame=frame)

    def stack(self, callback):
        self.send("stack", callback)

    def evaluate(self, expression, callback):
        self.send("evaluate", callback, expression=expression)

    def set_breakpoints(self, breakpoints):
        self.breakpoints = list(breakpoints)
        self.request("breakpoints", breakpoints=[{"line": line} for line in self.breakpoints])
