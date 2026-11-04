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
import zlib

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
    checks.check("it names the renderer it used", "with gl (" in rendered.stdout,
                 rendered.stdout[-200:])
    written = sorted(name for name in os.listdir(work) if name.endswith(".png"))
    checks.check("it wrote one PNG per frame", len(written) == 3, str(written))
    if len(written) == 3:
        header = png_header(os.path.join(work, written[0]))
        checks.check("the PNG is a 640x480 image", header is not None and header[0] == 640
                     and header[1] == 480, str(header))
        payloads = {open(os.path.join(work, name), "rb").read() for name in written}
        # The OpenGL mock paints the first vertex's colour and nothing else, so
        # a frame only differs from another when the first triangle it is given
        # differs. The cube culls the faces pointing away from the camera, and
        # two of three views can start with a face of the same colour - the text
        # views above are what proves the cube turns.
        checks.check("the frames are not all identical", len(payloads) >= 2, str(len(payloads)))

    # The Direct3D 12 path, against the mock that now checks every interface id
    # the way Windows does. This is the half of the example that cannot run on
    # Linux for real, and the half that reported "map has no method" and then
    # 0x80004002 from a user's machine.
    d3d = os.path.join(work, "libd3d12.so")
    build_d3d = subprocess.run([os.environ.get("CC", "cc"), "-shared", "-fPIC", "-o", d3d,
                                os.path.join(ROOT, "tools", "d3d12", "mock_d3d12.c")],
                               cwd=ROOT, capture_output=True, text=True)
    checks.check("the mock Direct3D 12 driver builds", build_d3d.returncode == 0,
                 build_d3d.stderr[-400:])
    if build_d3d.returncode == 0:
        environment_d3d = dict(os.environ, TERN_D3D12_LIBRARY=d3d, TERN_DXGI_LIBRARY=d3d,
                               TERN_D3DCOMPILER_LIBRARY=d3d)
        direct = subprocess.run(CLI + [example, "--frames", "2", "--out", os.path.join(work, "d3d")],
                                cwd=ROOT, capture_output=True, text=True, env=environment_d3d)
        checks.check("the cube renders through Direct3D 12", direct.returncode == 0
                     and "with d3d12" in direct.stdout, (direct.stderr or direct.stdout)[-400:])
        checks.check("every Direct3D interface id is the one Windows expects",
                     "E_NOINTERFACE" not in direct.stderr, direct.stderr[-400:])
        # A barrier that names the wrong "before" state is undefined behaviour on
        # real hardware: the driver is entitled to drop the device, which is how
        # 0x887A0005 turned up in save_frame far from the call that caused it.
        checks.check("no barrier lies about the resource state",
                     "STATE MISMATCH" not in (direct.stdout + direct.stderr),
                     (direct.stdout + direct.stderr)[-400:])

    # Every example runs, so the diagnostic is run too: it is the program a
    # user is told to run when a Direct3D call fails on their machine, and it
    # would be no use if it had only ever been compiled.
    diagnostic = subprocess.run(CLI + [os.path.join(ROOT, "examples", "d3d12_diagnose.tern")],
                                cwd=ROOT, capture_output=True, text=True,
                                env=dict(environment_d3d,
                                         TERN_D3D12_PNG=os.path.join(work, "diagnose.png")))
    checks.check("the diagnostic runs every step", diagnostic.returncode == 0
                 and "done" in diagnostic.stdout, (diagnostic.stderr or diagnostic.stdout)[-400:])
    checks.check("and reports the binding it is running",
                 "binding: " in diagnostic.stdout, diagnostic.stdout[-300:])

    # Point the driver at something that is not a driver: the availability probe
    # itself throws, and the example still has to finish with its text frames.
    broken = subprocess.run(CLI + [example, "--frames", "1", "--out", os.path.join(work, "broken")],
                            cwd=ROOT, capture_output=True, text=True,
                            env=dict(os.environ, TERN_GL_LIBRARY="/nonexistent/libEGL.so.1"))
    checks.check("an unusable backend is a sentence, not a traceback",
                 broken.returncode == 0 and "Traceback" not in (broken.stdout + broken.stderr),
                 (broken.stderr or broken.stdout)[-400:])
    checks.check("and it says which backend it could not use",
                 "OpenGL is not usable here" in broken.stdout or "no GPU renderer" in broken.stdout,
                 broken.stdout[-300:])

    help_run = subprocess.run(CLI + [example, "--help"], cwd=ROOT,
                              capture_output=True, text=True, env=environment)
    checks.check("--help explains the options", "--ascii" in help_run.stdout
                 and "--frames" in help_run.stdout, help_run.stdout[:200])

    # A machine with no window layer gets the frames instead of a failure, and
    # says so when a window was what was asked for. This is the path a Linux
    # user, a headless build agent and this test all take.
    windowed = subprocess.run(CLI + [example, "--window", "--size", "40x20"],
                              cwd=ROOT, capture_output=True, text=True, env=environment)
    checks.check("asking for a window without one still finishes", windowed.returncode == 0
                 and "Traceback" not in (windowed.stdout + windowed.stderr),
                 (windowed.stderr or windowed.stdout)[-400:])
    checks.check("and it says where the window went",
                 "no window on this machine" in windowed.stdout, windowed.stdout[-300:])

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

    # Same claim as above, but through the native runtime, whose Direct3D 12 code
    # is a separate implementation and so can drift on its own.
    if build_d3d.returncode == 0:
        native_d3d = subprocess.run([binary, "--frames", "2", "--out", os.path.join(work, "native_d3d")],
                                    cwd=ROOT, capture_output=True, text=True, env=environment_d3d)
        checks.check("the compiled cube renders through Direct3D 12", native_d3d.returncode == 0
                     and "with d3d12" in native_d3d.stdout, (native_d3d.stderr or native_d3d.stdout)[-400:])
        checks.check("and it does not lie about the resource state either",
                     "STATE MISMATCH" not in (native_d3d.stdout + native_d3d.stderr),
                     (native_d3d.stdout + native_d3d.stderr)[-400:])

def build_native(source, output):
    """Compile a .tern example with the native backend, or return None."""
    build = subprocess.run([PYTHON, os.path.join(ROOT, "native", "ternc.py"), source, "-o", output],
                           cwd=ROOT, capture_output=True, text=True)
    return output if build.returncode == 0 and os.path.exists(output) else None


def png_size(path):
    """(width, height) from a PNG's IHDR, and its chunk types in order."""
    with open(path, "rb") as handle:
        data = handle.read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        return None
    position = 8
    width = height = 0
    kinds = []
    while position + 12 <= len(data):
        length = struct.unpack(">I", data[position:position + 4])[0]
        kind = data[position + 4:position + 8]
        body = data[position + 8:position + 8 + length]
        crc = struct.unpack(">I", data[position + 8 + length:position + 12 + length])[0]
        if zlib.crc32(kind + body) & 0xFFFFFFFF != crc:
            return None
        kinds.append(kind)
        if kind == b"IHDR":
            width, height = struct.unpack(">II", body[:8])
        position += 12 + length
    return width, height, kinds, data


def test_raytracer(checks):
    """The CPU ray tracer: its PNG is a real PNG, and both back ends agree byte for byte."""
    work = tempfile.mkdtemp(prefix="tern-ray-")
    example = os.path.join(ROOT, "examples", "raytracer.tern")
    interpreted = os.path.join(work, "interpreter.png")

    run = subprocess.run(CLI + [example, "--out", interpreted, "--size", "48x36"], cwd=ROOT,
                         capture_output=True, text=True)
    checks.check("the raytracer runs", run.returncode == 0, (run.stderr or run.stdout)[-400:])
    if run.returncode != 0:
        return
    checks.check("it says how long the tracing took", "traced" in run.stdout
                 and "rays in" in run.stdout, run.stdout[-300:])

    parsed = png_size(interpreted) if os.path.exists(interpreted) else None
    checks.check("it writes a file", parsed is not None, "no PNG at " + interpreted)
    if parsed is None:
        return
    # Written by hand, so every claim about it is worth checking: the CRC of
    # each chunk, the size in the header, and the order the chunks come in.
    width, height, kinds, data = parsed
    checks.check("the header says 48x36", (width, height) == (48, 36), f"{width}x{height}")
    checks.check("the chunks are IHDR, IDAT, IEND", kinds == [b"IHDR", b"IDAT", b"IEND"], str(kinds))
    checks.check("it is 8 bits and truecolour", data[24] == 8 and data[25] == 2,
                 f"depth {data[24]}, colour type {data[25]}")

    # The picture has to be a picture: a floor, a sky and four spheres is
    # thousands of distinct colours, and a black or blank frame is one.
    raw = zlib.decompress(_idat_payload(data))
    stride = 1 + width * 3
    colours = set()
    for y in range(height):
        row = raw[y * stride + 1:(y + 1) * stride]
        for x in range(width):
            colours.add(row[x * 3:x * 3 + 3])
    checks.check("the image is not blank", len(colours) > 50, f"{len(colours)} colours")
    # The sky is the top row and the floor is the bottom one, so the two ends
    # cannot be the same colour unless the camera is looking at nothing.
    top = raw[1:4]
    bottom = raw[(height - 1) * stride + 1:(height - 1) * stride + 4]
    checks.check("the sky and the floor differ", top != bottom, f"{top} vs {bottom}")

    # Parallax: the tracer is deterministic, so a second run is the same file,
    # and a different size is a different file.
    again = os.path.join(work, "again.png")
    subprocess.run(CLI + [example, "--out", again, "--size", "48x36"], cwd=ROOT,
                   capture_output=True, text=True)
    checks.check("the same scene traces to the same bytes",
                 open(again, "rb").read() == data)

    native = build_native(example, os.path.join(work, "raytracer"))
    if native is None:
        checks.check("the raytracer compiles to a binary", False, "ternc.py failed")
        return
    checks.check("the raytracer compiles to a binary", True)
    compiled = os.path.join(work, "native.png")
    run = subprocess.run([native, "--out", compiled, "--size", "48x36"], cwd=ROOT,
                         capture_output=True, text=True)
    checks.check("the compiled raytracer runs", run.returncode == 0,
                 (run.stderr or run.stdout)[-400:])
    checks.check("both back ends produce the same PNG",
                 os.path.exists(compiled) and open(compiled, "rb").read() == data)


def _idat_payload(data):
    """The zlib stream inside the first IDAT chunk."""
    position = data.index(b"IDAT") - 4
    length = struct.unpack(">I", data[position:position + 4])[0]
    return data[position + 8:position + 8 + length]

def build_mock_d3d12(work):
    """The mock Direct3D 12 driver, or None."""
    mock = os.path.join(work, "libd3d12.so")
    built = subprocess.run([os.environ.get("CC", "cc"), "-shared", "-fPIC", "-o", mock,
                            os.path.join(ROOT, "tools", "d3d12", "mock_d3d12.c")],
                           cwd=ROOT, capture_output=True, text=True)
    return mock if built.returncode == 0 else None


def test_shaders(checks):
    """The two shader examples: what they generate, and what the HLSL looks like."""
    sys.path.insert(0, os.path.join(ROOT, "tools", "d3d12"))
    import hlsl_lint

    work = tempfile.mkdtemp(prefix="tern-shaders-")
    mock = build_mock_d3d12(work)
    checks.check("the mock Direct3D 12 driver builds for the shader examples", mock is not None)
    if mock is None:
        return
    environment = dict(os.environ, TERN_D3D12_LIBRARY=mock, TERN_DXGI_LIBRARY=mock,
                       TERN_D3DCOMPILER_LIBRARY=mock)

    showcase = os.path.join(ROOT, "examples", "shader_showcase.tern")
    out = os.path.join(work, "frames")
    hlsl = os.path.join(work, "hlsl")
    run = subprocess.run(CLI + [showcase, "--out", out, "--emit-hlsl", hlsl, "--size", "64x48"],
                         cwd=ROOT, capture_output=True, text=True, env=environment)
    checks.check("the shader showcase runs", run.returncode == 0,
                 (run.stderr or run.stdout)[-400:])
    checks.check("it compiles every shader", "compiled 4 pixel shaders" in run.stdout,
                 run.stdout[-300:])
    written = sorted(os.listdir(out)) if os.path.isdir(out) else []
    checks.check("it writes one image per shader", len(written) == 4, str(written))

    sources = sorted(os.listdir(hlsl)) if os.path.isdir(hlsl) else []
    checks.check("it writes one HLSL source per shader", len(sources) == 4, str(sources))
    # The shader is compiled by d3dcompiler on Windows, which is not here, so
    # the generated text is checked for the mistakes a line-list generator
    # makes: an unclosed brace, a call to a name that is not defined, an
    # integer where a float is meant. This is not a compiler and does not claim
    # to be one - docs/status.md says the same.
    problems = []
    for name in sources:
        problems += hlsl_lint.check(open(os.path.join(hlsl, name), encoding="utf-8").read(),
                                    name)
    checks.check("the generated HLSL passes the structural check", not problems,
                 "; ".join(problems[:4]))

    frames = subprocess.run(CLI + [showcase, "--out", os.path.join(work, "moving"),
                                   "--size", "32x24", "--frames", "2"],
                            cwd=ROOT, capture_output=True, text=True, env=environment)
    checks.check("it renders more than one frame", frames.returncode == 0
                 and len(os.listdir(os.path.join(work, "moving"))) == 8,
                 (frames.stderr or frames.stdout)[-300:])

    traced = os.path.join(ROOT, "examples", "d3d12_raytrace.tern")
    ray_source = os.path.join(work, "shot.hlsl")
    ray_png = os.path.join(work, "shot.png")
    # --window=false on purpose: without a window the example is one frame and a
    # file, so this test cannot open a window on a machine that has a display and
    # stop there waiting for somebody to close it.
    run = subprocess.run(CLI + [traced, "--out", ray_png, "--emit-hlsl", ray_source,
                                "--size", "64x48", "--window=false"],
                         cwd=ROOT, capture_output=True, text=True, env=environment)
    checks.check("the GPU ray tracer runs", run.returncode == 0,
                 (run.stderr or run.stdout)[-400:])
    checks.check("it reports the spheres it was given", "4 spheres" in run.stdout,
                 run.stdout[-300:])
    checks.check("it writes its image", os.path.exists(ray_png))
    checks.check("the emitted shader has the scene baked into it",
                 os.path.exists(ray_source)
                 and "SPHERES[4][2]" in open(ray_source, encoding="utf-8").read(),
                 "no baked scene")

    # The camera is the part that changes every frame, so it is not in the
    # source: it arrives through the root signature. A shader with the old baked
    # EYE would compile once and then never move, and the window would show a
    # still picture however far the mouse dragged.
    source = open(ray_source, encoding="utf-8").read() if os.path.exists(ray_source) else ""
    checks.check("the camera comes from the root signature, not from the source",
                 "cbuffer Camera : register(b0)" in source
                 and "CAM_EYE" in source and "LOOK_AT" not in source,
                 source[:300])
    checks.check("and the shader reads all four camera vectors",
                 all(name in source for name in ("CAM_RIGHT", "CAM_UP", "CAM_FORWARD", "CAM_EYE")),
                 source[:300])

    # The window is what a person runs, and it has to be offerable without
    # opening one: --help is the only place the options are listed.
    help_run = subprocess.run(CLI + [traced, "--help"], cwd=ROOT,
                              capture_output=True, text=True, env=environment)
    checks.check("--help lists the camera and the window",
                 all(option in help_run.stdout
                     for option in ("--window", "--seconds", "--yaw", "--distance")),
                 help_run.stdout[:400])

    # A camera that cannot be set is not a camera. The picture cannot prove that
    # here - the mock compiler hands back a stand-in shader, so every frame is
    # the same vertices on the same background whatever the constants say - but
    # the values themselves can, and tools/test-d3d12.sh reads the constants
    # every frame carried out of the mock's trace: four draws, four different
    # cameras, both back ends the same. What is left for this test is that the
    # options are accepted and still produce a file.
    for option, value in (("--yaw", "1.2"), ("--pitch", "0.6"), ("--distance", "6.5")):
        target = os.path.join(work, f"camera{option[2:]}.png")
        result = subprocess.run(CLI + [traced, "--out", target, "--size", "48x36",
                                       "--window=false", option, value],
                                cwd=ROOT, capture_output=True, text=True, env=environment)
        checks.check(f"{option} is a camera the ray tracer accepts",
                     result.returncode == 0 and os.path.getsize(target) > 0
                     if os.path.exists(target) else False,
                     (result.stderr or result.stdout)[-300:])
    ray_problems = hlsl_lint.check(open(ray_source, encoding="utf-8").read(), "shot.hlsl") \
        if os.path.exists(ray_source) else ["the shader was not written"]
    checks.check("the ray tracer's HLSL passes the structural check", not ray_problems,
                 "; ".join(ray_problems[:4]))

    # The check that was missing. The ray tracer used to call itself once per
    # reflection, which passed every other lint and then failed on the GPU with
    # "error X3500: 'trace': recursive functions not allowed in ps_5_0".
    recursive = (
        "float3 trace(float3 origin, float3 direction, int depth) {\n"
        "    return depth > 0 ? trace(origin, direction, depth - 1) : origin;\n"
        "}\n"
        "float4 main(float4 pixel : SV_Position) : SV_Target {\n"
        "    return float4(trace(pixel.xyz, pixel.xyz, 3), 1.0);\n"
        "}\n")
    problems = hlsl_lint.check(recursive, "recursive.hlsl")
    checks.check("the lint rejects a shader that calls itself",
                 any("recursive" in problem and "X3500" in problem for problem in problems),
                 str(problems[:2]))

    mutual = (
        "float3 ping(float3 p) { return pong(p); }\n"
        "float3 pong(float3 p) { return ping(p); }\n"
        "float4 main(float4 pixel : SV_Position) : SV_Target {\n"
        "    return float4(ping(pixel.xyz), 1.0);\n"
        "}\n")
    checks.check("and one that recurses through another function",
                 any("recursive" in problem for problem in hlsl_lint.check(mutual, "mutual.hlsl")),
                 str(hlsl_lint.check(mutual, "mutual.hlsl")[:2]))
    checks.check("an ordinary shader is still accepted",
                 not hlsl_lint.check(
                     "float3 lift(float3 p) { return p * 2.0; }\n"
                     "float4 main(float4 pixel : SV_Position) : SV_Target {\n"
                     "    return float4(lift(pixel.xyz), 1.0);\n"
                     "}\n", "plain.hlsl"))

    # A run that produced no image is a failed run, and the status has to say
    # so: a build step has nothing else to go on. The mock compiler refuses any
    # source with FAIL in it, which is how this is checked without a GPU.
    failing = os.path.join(work, "failing.tern")
    with open(failing, "w", encoding="utf-8") as handle:
        handle.write(
            'import "std:d3d12";\n'
            'import "std:os";\n'
            '\n'
            'fn main() {\n'
            '    if (!d3d12.available()) { return; }\n'
            '    let target = d3d12.create(8, 8);\n'
            '    try {\n'
            '        d3d12.pipeline(target,\n'
            '            "float4 main(float4 p : SV_Position) : SV_Position { return p; }",\n'
            '            "float4 main(float4 p : SV_Position) : SV_Target { return float4(1.0, 0.0, 0.0, 1.0); } // FAIL");\n'
            '    } catch (error) {\n'
            '        println("the shader did not compile");\n'
            '        d3d12.close(target);\n'
            '        os.exit(1);\n'
            '    }\n'
            '}\n'
            '\n'
            'main();\n')

    failed_run = subprocess.run(CLI + [failing], cwd=ROOT, capture_output=True, text=True,
                                env=environment)
    checks.check("a shader that does not compile exits non-zero", failed_run.returncode == 1,
                 f"exit {failed_run.returncode}: {(failed_run.stdout + failed_run.stderr)[-200:]}")

    # os.exit is how a script says so; the plain path is checked here too, so a
    # missing status cannot hide behind the d3d12 ones.
    exits = os.path.join(work, "exits.tern")
    with open(exits, "w", encoding="utf-8") as handle:
        handle.write('import "std:os";\nprintln("before");\nos.exit(7);\n')
    left = subprocess.run(CLI + [exits], cwd=ROOT, capture_output=True, text=True)
    checks.check("os.exit sets the process status", left.returncode == 7,
                 f"exit {left.returncode}: {left.stdout!r}")
    checks.check("and the output written before it is not lost", "before" in left.stdout,
                 repr(left.stdout))

    # Without a driver both examples have to be a sentence, not a traceback:
    # that is what a Linux user sees, and it is most of the audience for these
    # two files.
    # --window=false for the ray tracer only: the showcase has no window options,
    # and the point of this pair is the message a missing driver produces.
    for source, name, extra in ((showcase, "showcase", []), (traced, "ray tracer", ["--window=false"])):
        absent = subprocess.run(CLI + [source] + extra, cwd=ROOT,
                                capture_output=True, text=True,
                                env=dict(os.environ, TERN_D3D12_LIBRARY="/nonexistent/libd3d12.so"))
        checks.check(f"the {name} without Direct3D 12 is a sentence, not a traceback",
                     absent.returncode == 0 and "not available" in absent.stdout
                     and "Traceback" not in absent.stdout + absent.stderr,
                     (absent.stderr or absent.stdout)[-400:])


def main(argv):
    verbose = "-v" in argv or "--verbose" in argv
    filters = [argument for argument in argv if not argument.startswith("-")]
    checks = Checks(verbose)

    groups = [("http", test_http), ("rcon", test_rcon), ("sftp", test_sftp),
              ("graphics", test_graphics), ("raytracer", test_raytracer),
              ("shaders", test_shaders), ("native", test_native_http)]
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
