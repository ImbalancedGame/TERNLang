#!/usr/bin/env python3
"""A debugger client, used by the tests and by Studio.

The debugger speaks one JSON object per line. This class sends commands and
reads replies and events, which is all either caller needs.
"""

import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))


def server_command():
    """How to start the debugger, as an argv list."""
    assembly = os.path.join(ROOT, "build", "TERN.Debug.dll")
    launcher = os.path.join(ROOT, "build", "dotnet")
    if os.path.exists(launcher):
        return [launcher, assembly]
    toolchain = os.environ.get("TERN_TOOLCHAIN_DIR", "/tmp/tern-toolchain")
    candidate = os.path.join(toolchain, "dotnet", "dotnet")
    if os.path.exists(candidate):
        return [candidate, assembly]
    return ["dotnet", assembly]


class DebugClient:
    def __init__(self, cwd=None):
        environment = dict(os.environ)
        environment.setdefault("DOTNET_ROOT", os.path.join(
            os.environ.get("TERN_TOOLCHAIN_DIR", "/tmp/tern-toolchain"), "dotnet"))
        self.process = subprocess.Popen(
            server_command(), cwd=cwd or ROOT, stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            bufsize=1, env=environment)
        self._next_id = 1
        self._pending = []          # events read while waiting for a response

    # ------------------------------------------------------------------ io

    def send(self, command, **arguments):
        message = {"id": self._next_id, "command": command, **arguments}
        self._next_id += 1
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()
        return message["id"]

    def read(self):
        """The next line from the debugger, as a dict; None at end of stream."""
        line = self.process.stdout.readline()
        if not line:
            return None
        return json.loads(line)

    def wait_for(self, predicate, limit=200):
        """Reads until a message satisfies `predicate`, or gives up.

        Messages that do not match are kept, so an event read while waiting for
        a response can still be asked for afterwards.
        """
        for index, message in enumerate(self._pending):
            if predicate(message):
                del self._pending[index]
                return message

        for _ in range(limit):
            message = self.read()
            if message is None:
                return None
            if predicate(message):
                return message
            self._pending.append(message)
        return None

    def wait_for_event(self, name, limit=200):
        return self.wait_for(
            lambda message: message.get("type") == "event" and message.get("event") == name,
            limit)

    def request(self, command, **arguments):
        """Sends a command and returns its response, skipping events."""
        identifier = self.send(command, **arguments)
        return self.wait_for(lambda message: message.get("id") == identifier)

    def launch(self, path, breakpoints=()):
        """Starts a program, with breakpoint line numbers."""
        return self.request("launch", program=os.path.abspath(path),
                            breakpoints=[{"line": line} for line in breakpoints])

    # -------------------------------------------------------------- lifecycle

    def close(self, timeout=10):
        try:
            self.process.stdin.close()
        except OSError:
            pass
        try:
            self.process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)
        return self.process.returncode

    @property
    def stderr(self):
        return self.process.stderr.read() if self.process.stderr else ""
