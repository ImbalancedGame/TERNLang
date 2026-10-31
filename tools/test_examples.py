#!/usr/bin/env python3
"""Runs the examples in `examples/` against servers this file brings with it.

An example that is never executed is a comment with syntax highlighting, so
this starts the HTTP server, talks to it with a real HTTP client, plays an RCON
server for the RCON example, drives the SFTP example's dry run — which is the
half of it that does not need a remote machine — and renders the rotating cube
through a mock GL driver, in both back ends.

    python3 tools/test_examples.py            run it
    python3 tools/test_examples.py -v         show each check
    python3 tools/test_examples.py http       only tests whose name contains "http"

The HTTP server is also run as a compiled binary, so the two back ends are
checked against the same client rather than one being trusted to behave.
"""

import os
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PYTHON = sys.executable


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
                print(f"       {detail}")


def free_port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


CLI = [os.path.join(ROOT, "tools", "tern")] if os.name != "nt" else ["cmd", "/c",
                                                                     os.path.join(ROOT, "tools", "tern.cmd")]


def tern(arguments, cwd=ROOT, timeout=120):
    """Run a script the way a user does: through the CLI launcher."""
    return subprocess.run(CLI + arguments, cwd=cwd, capture_output=True, text=True, timeout=timeout)


def wait_for_port(port, deadline=20.0):
    end = time.time() + deadline
    while time.time() < end:
        with socket.socket() as probe:
            probe.settimeout(0.25)
            if probe.connect_ex(("127.0.0.1", port)) == 0:
                return True
        time.sleep(0.05)
    return False


# --------------------------------------------------------------------------- HTTP

def fetch(port, path, method="GET", body=None):
    request = urllib.request.Request(f"http://127.0.0.1:{port}{path}", method=method,
                                     data=body.encode() if body is not None else None)
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return response.status, response.read().decode(), dict(response.headers)
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode(), dict(error.headers)


def test_http(checks, native_binary=None):
    """The HTTP server, over a real socket, as a browser would talk to it."""
    port = free_port()
    server = subprocess.Popen(CLI + [os.path.join(ROOT, "examples", "http_server.tern"),
                                     str(port), "127.0.0.1"],
                              cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    try:
        if not wait_for_port(port):
            checks.check("the HTTP server starts", False, "nothing listening after 20 s")
            return
        checks.check("the HTTP server starts", True)

        status, body, headers = fetch(port, "/")
        checks.check("GET / is 200", status == 200, str(status))
        checks.check("GET / is HTML", "text/html" in headers.get("Content-Type", ""),
                     headers.get("Content-Type", ""))
        checks.check("GET / is the site, not an error page", "TERN says hello" in body, body[:120])
        checks.check("the response closes the connection",
                     headers.get("Connection") == "close", str(headers.get("Connection")))
        checks.check("the response says who served it", headers.get("X-Served-By") == "TERN",
                     str(headers.get("X-Served-By")))

        status, body, _ = fetch(port, "/api/time")
        checks.check("GET /api/time is JSON from the clock",
                     status == 200 and '"now"' in body and '"milliseconds"' in body, body[:120])

        status, body, _ = fetch(port, "/api/upper", "POST", "hello there")
        checks.check("POST body arrives intact", '"received": "hello there"' in body, body[:160])
        checks.check("POST body is upper-cased", '"upper": "HELLO THERE"' in body, body[:160])

        status, body, _ = fetch(port, "/api/echo?message=hi%20there%2C%20caf%C3%A9")
        checks.check("query strings are decoded, UTF-8 included",
                     '"youSaid": "hi there, café"' in body, body[:200])

        status, body, _ = fetch(port, "/hello/ada")
        checks.check("a path parameter reaches the handler", "<b>ada</b>" in body, body[:160])

        status, body, _ = fetch(port, "/hello/%3Cscript%3E")
        checks.check("user input is escaped, not echoed", "<script>" not in body, body[:160])

        status, _, _ = fetch(port, "/nope")
        checks.check("an unknown path is 404", status == 404, str(status))

        # A request with no headers at all, written by hand: the parser must not
        # need anything a browser happens to send.
        with socket.create_connection(("127.0.0.1", port), timeout=5) as raw:
            raw.sendall(b"GET /api/time HTTP/1.0\r\n\r\n")
            reply = b""
            while True:
                piece = raw.recv(4096)
                if not piece:
                    break
                reply += piece
        checks.check("a bare HTTP/1.0 request is answered", reply.startswith(b"HTTP/1.1 200"), reply[:60])
        head, _, payload = reply.partition(b"\r\n\r\n")
        declared = [line for line in head.split(b"\r\n") if line.lower().startswith(b"content-length:")]
        checks.check("the body matches Content-Length",
                     bool(declared) and int(declared[0].split(b":")[1]) == len(payload),
                     f"{declared} vs {len(payload)}")
    finally:
        server.terminate()
        try:
            output, _ = server.communicate(timeout=10)
        except subprocess.TimeoutExpired:
            server.kill()
            output, _ = server.communicate()
        if checks.verbose:
            print("       server said: " + output.replace("\n", "\n       "))

    checks.check("the server logged the requests", "GET  /api/time" in output, output[-300:])


# --------------------------------------------------------------------------- RCON

class MockRcon(threading.Thread):
    """The server half of Source RCON, as the protocol defines it."""

    def __init__(self, password):
        super().__init__(daemon=True)
        self.password = password
        self.port = free_port()
        self.commands = []
        self.ready = threading.Event()
        self._stop = threading.Event()
        self.socket = socket.socket()
        self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.socket.bind(("127.0.0.1", self.port))
        self.socket.listen(4)
        self.socket.settimeout(0.2)

    def run(self):
        self.ready.set()
        while not self._stop.is_set():
            try:
                connection, _ = self.socket.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            with connection:
                self.serve(connection)

    def serve(self, connection):
        connection.settimeout(5)
        while not self._stop.is_set():
            header = self.read_exactly(connection, 4)
            if not header:
                return
            length = struct.unpack("<i", header)[0]
            body = self.read_exactly(connection, length)
            if body is None:
                return
            request_id, kind = struct.unpack("<ii", body[:8])
            text = body[8:-2].decode("utf-8", "replace")
            if kind == 3:                       # auth
                accepted = text == self.password
                # The protocol's refusal is the id: the server echoes the
                # request id on success and answers -1 on a bad password.
                connection.sendall(self.packet(request_id if accepted else -1, 2, ""))
                if not accepted:
                    return
                continue
            self.commands.append(text)
            self.reply(connection, request_id, text)

    def reply(self, connection, request_id, command):
        if command == "list":
            connection.sendall(self.packet(request_id, 0, "ada\n" * 600))   # > 4096 bytes
        elif command.startswith("say "):
            connection.sendall(self.packet(request_id, 0, "SERVER: " + command[4:]))
        elif command == "status":
            connection.sendall(self.packet(request_id, 0, "hostname: tern-test\nplayers: 2"))
        else:
            connection.sendall(self.packet(request_id, 0, "unknown command"))
        connection.sendall(self.packet(request_id + 100, 0, ""))

    @staticmethod
    def read_exactly(connection, count):
        data = b""
        while len(data) < count:
            piece = connection.recv(count - len(data))
            if not piece:
                return None
            data += piece
        return data

    @staticmethod
    def packet(request_id, kind, text):
        payload = struct.pack("<ii", request_id, kind) + text.encode() + b"\x00\x00"
        return struct.pack("<i", len(payload)) + payload

    def stop(self):
        self._stop.set()
        self.socket.close()


def test_rcon(checks):
    server = MockRcon("hunter2")
    server.start()
    try:
        result = tern(["run", os.path.join(ROOT, "examples", "rcon.tern"),
                       "127.0.0.1", str(server.port), "hunter2", "status", "say hello"])
        checks.check("the RCON example runs", result.returncode == 0, result.stderr[-400:])
        checks.check("the RCON example logs in", "connected to 127.0.0.1" in result.stdout,
                     result.stdout[:200])
        checks.check("a command's answer is printed", "hostname: tern-test" in result.stdout,
                     result.stdout[-300:])
        checks.check("arguments with spaces survive", "SERVER: hello" in result.stdout,
                     result.stdout[-300:])
        checks.check("the session is closed at the end", "session closed" in result.stdout,
                     result.stdout[-200:])
        sent = [command for command in server.commands if command]
        checks.check("the server saw both commands", sent == ["status", "say hello"], str(server.commands))

        refused = tern(["run", os.path.join(ROOT, "examples", "rcon.tern"),
                        "127.0.0.1", str(server.port), "wrong", "status"])
        checks.check("a refused password is reported, not raised",
                     "cannot log in" in refused.stderr and "AuthenticationError" not in refused.stderr,
                     (refused.stderr or refused.stdout)[-300:])

        down = tern(["run", os.path.join(ROOT, "examples", "rcon.tern"),
                     "127.0.0.1", str(free_port()), "hunter2", "status"])
        checks.check("a refused connection says so", "cannot log in" in down.stderr,
                     down.stderr[-200:])

        # A big answer arrives in several packets and has to be joined.
        many = tern(["run", os.path.join(ROOT, "examples", "rcon.tern"),
                     "127.0.0.1", str(server.port), "hunter2", "list"])
        checks.check("a split response is joined",
                     many.stdout.count("ada") > 500, str(many.stdout.count("ada")))
    finally:
        server.stop()

    usage = tern(["run", os.path.join(ROOT, "examples", "rcon.tern")])
    checks.check("no arguments prints usage", "usage: tern run examples/rcon.tern" in usage.stdout,
                 usage.stdout[:200])


# --------------------------------------------------------------------------- SFTP

def test_sftp(checks):
    work = tempfile.mkdtemp(prefix="tern-sftp-")
    dry = tern(["run", os.path.join(ROOT, "examples", "sftp_deploy.tern"), "--dry-run",
                "deploy@example.com:/srv/app"], cwd=work)
    checks.check("the SFTP example runs without a server",
                 dry.returncode == 0, (dry.stderr or dry.stdout)[-400:])
    checks.check("it prints the command line sftp would get",
                 "StrictHostKeyChecking=accept-new" in dry.stdout and "-b" in dry.stdout,
                 dry.stdout[:400])
    checks.check("it prints the batch", "put \"tern-deploy.txt\"" in dry.stdout, dry.stdout[-400:])
    checks.check("it names the target it would use",
                 "host:      example.com:22" in dry.stdout and "user:      deploy" in dry.stdout,
                 dry.stdout[:400])
    checks.check("the dry run sends nothing", "(nothing was sent: --dry-run)" in dry.stdout,
                 dry.stdout[-200:])
    checks.check("the payload file was written", os.path.exists(os.path.join(work, "tern-deploy.txt")))

    # A real run against a port with nothing on it: the failure has to be a
    # sentence, not a traceback.
    result = tern(["run", os.path.join(ROOT, "examples", "sftp_deploy.tern"),
                   "deploy@127.0.0.1:/tmp", "/nonexistent-key"], cwd=work, timeout=180)
    checks.check("a missing key is reported", "upload failed" in result.stderr,
                 (result.stderr or result.stdout)[-300:])
    checks.check("and it is not a traceback", "Traceback" not in (result.stderr or ""),
                 result.stderr[-300:])

    help_run = tern(["run", os.path.join(ROOT, "examples", "sftp_deploy.tern"), "--help"], cwd=work)
    checks.check("--help explains the arguments", "--dry-run" in help_run.stdout, help_run.stdout[:200])


# --------------------------------------------------------------------------- native

def test_native_http(checks):
    """The same server, compiled to C, answering the same client."""
    work = tempfile.mkdtemp(prefix="tern-native-")
    binary = os.path.join(work, "http_server")
    build = subprocess.run([PYTHON, os.path.join(ROOT, "native", "ternc.py"),
                            os.path.join(ROOT, "examples", "http_server.tern"), "-o", binary],
                           cwd=ROOT, capture_output=True, text=True, timeout=900)
    if build.returncode != 0:
        checks.check("the HTTP server compiles with the native backend", False, build.stderr[-600:])
        return
    checks.check("the HTTP server compiles with the native backend", True)

    port = free_port()
    server = subprocess.Popen([binary, str(port), "127.0.0.1"], cwd=work,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    try:
        if not wait_for_port(port):
            checks.check("the compiled server listens", False)
            return
        checks.check("the compiled server listens", True)
        status, body, _ = fetch(port, "/api/echo?message=compiled")
        checks.check("the compiled server answers", status == 200 and '"youSaid": "compiled"' in body,
                     body[:160])
        status, body, _ = fetch(port, "/api/upper", "POST", "native")
        checks.check("the compiled server reads request bodies",
                     '"upper": "NATIVE"' in body, body[:160])
    finally:
        server.terminate()
        try:
            server.communicate(timeout=10)
        except subprocess.TimeoutExpired:
            server.kill()
            server.communicate()


# ------------------------------------------------------------------------- graphics

def png_header(path):
    """Width, height and byte count from a PNG's IHDR, or None if it is not one."""
    try:
        with open(path, "rb") as handle:
            data = handle.read(33)
    except OSError:
        return None
    if len(data) < 33 or data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        return None
    width, height = struct.unpack(">II", data[16:24])
    return width, height, os.path.getsize(path)


def test_graphics(checks):
    """The rotating cube: the text rasteriser in both back ends, PNGs through a mock driver."""
    work = tempfile.mkdtemp(prefix="tern-cube-")
    mock = os.path.join(work, "libEGL.so.1")
    build = subprocess.run([os.environ.get("CC", "cc"), "-shared", "-fPIC", "-o", mock,
                            os.path.join(ROOT, "tools", "gl", "mock_driver.c")],
                           cwd=ROOT, capture_output=True, text=True)
    checks.check("the mock GL driver builds", build.returncode == 0, build.stderr[-400:])
    if build.returncode != 0:
        return
    environment = dict(os.environ, TERN_GL_LIBRARY=mock)
    example = os.path.join(ROOT, "examples", "rotating_cube.tern")

    text = subprocess.run(CLI + [example, "--ascii", "--size", "60x30"], cwd=ROOT,
                          capture_output=True, text=True, env=environment)
    checks.check("the cube example runs", text.returncode == 0,
                 (text.stderr or text.stdout)[-400:])
    views = [view for view in text.stdout.split("yaw ") if view.strip()]
    checks.check("it draws four views", len(views) == 4, str(len(views)))
    checks.check("each view has a cube in it",
                 all(any(character not in " \n" for character in view) for view in views))
    checks.check("the views are not all the same", len(set(views)) == len(views))

    # A cube turning: opposite corners of the turn are different, and so are
    # neighbouring ones.
    checks.check("the last view has turned away from the first", views[0] != views[-1])

    rendered = subprocess.run(CLI + [example, "--frames", "3", "--out", work], cwd=ROOT,
                              capture_output=True, text=True, env=environment)
    checks.check("the renderer path runs", rendered.returncode == 0,
                 (rendered.stderr or rendered.stdout)[-400:])
    checks.check("it names the renderer it used", "with gl." in rendered.stdout,
                 rendered.stdout[-200:])
    written = sorted(name for name in os.listdir(work) if name.endswith(".png"))
    checks.check("it wrote one PNG per frame", len(written) == 3, str(written))
    if len(written) == 3:
        header = png_header(os.path.join(work, written[0]))
        checks.check("the PNG is a 640x480 image", header is not None and header[0] == 640
                     and header[1] == 480, str(header))
        payloads = {open(os.path.join(work, name), "rb").read() for name in written}
        checks.check("the frames differ", len(payloads) == 3)

    help_run = subprocess.run(CLI + [example, "--help"], cwd=ROOT,
                              capture_output=True, text=True, env=environment)
    checks.check("--help explains the options", "--ascii" in help_run.stdout
                 and "--frames" in help_run.stdout, help_run.stdout[:200])

    binary = os.path.join(work, "cube")
    native = subprocess.run([PYTHON, os.path.join(ROOT, "native", "ternc.py"), example,
                             "-o", binary], cwd=ROOT, capture_output=True, text=True, timeout=900)
    checks.check("the cube example compiles natively", native.returncode == 0, native.stderr[-600:])
    if native.returncode != 0:
        return
    compiled = subprocess.run([binary, "--ascii", "--size", "60x30"], cwd=ROOT,
                              capture_output=True, text=True, env=environment)
    checks.check("the compiled cube prints the same views", compiled.stdout == text.stdout,
                 "outputs differ")


def main(argv):
    verbose = "-v" in argv or "--verbose" in argv
    filters = [argument for argument in argv if not argument.startswith("-")]
    checks = Checks(verbose)

    groups = [("http", test_http), ("rcon", test_rcon), ("sftp", test_sftp),
              ("graphics", test_graphics), ("native", test_native_http)]
    for name, function in groups:
        if filters and not any(f in name for f in filters):
            continue
        if verbose:
            print(f"== {name}")
        function(checks)

    print()
    print(f"passed: {checks.passed}  failed: {len(checks.failed)}")
    for name in checks.failed:
        print(f"  failed: {name}")
    return 1 if checks.failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
