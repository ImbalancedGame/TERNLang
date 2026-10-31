"""Content for the MSDN-style reference.

Every namespace and function gets its prose here; the signatures, arity and the
one-line summary come from the engine itself (`tern docs --json`), so the
reference cannot describe a function that no longer exists. Anything missing
here still gets an entry, built from the engine's own description.

Fields per function:

    summary   one sentence, shown under the heading
    params    list of (name, type, description)
    returns   (type, description) for the return value, or None for nil
    errors    list of (error name, when it is raised)
    remarks   optional Markdown paragraph
    example   list of TERN source lines
    see       list of related names
"""

NAMESPACES = {
    "net": {
        "title": "net",
        "summary": "Sockets, framing and datagrams.",
        "intro": """
`net` sends values, not bytes. `net.send` encodes any TERN value — nil, a
number, a string, a list, a map, a struct, a range — and writes it as one frame;
`net.receive` reads a frame and decodes it. The same wire format is used by the
interpreter and by compiled binaries, so the two can talk to each other.

Every connection opens with a hello frame (see `net.listen`), which carries the
protocol version, the frame ceiling and, optionally, a token. A listener can
therefore refuse a stranger before the script ever sees a connection.
""",
        "example": [
            'import "std:net";',
            "",
            "let server = net.listen(0, {\"token\": \"s3cret\"});",
            "let port = net.port(server);",
            "",
            "let client = net.connect(\"127.0.0.1\", port, {\"token\": \"s3cret\", \"timeout\": 2000});",
            "let peer = net.accept(server);",
            "net.send(client, {\"kind\": \"ping\"});",
            "println(net.receive(peer));",
        ],
    },
    "http": {
        "title": "http",
        "summary": "An HTTP/1.1 client, including https.",
        "intro": """
`http` speaks HTTP/1.1 on a socket it opens itself; there is no library behind
it. `https://` URLs are wrapped in TLS with the same transport `net.connect`
uses, so the certificate is checked against the system store unless `ca` names a
PEM bundle or `verify` is false.

A response is a map with `status`, `reason`, `version`, `headers` (lower-cased
keys) and `body`.
""",
        "example": [
            'import "std:http";',
            "",
            'let response = http.get("https://example.com/", {"timeout": 5000});',
            'println(response["status"], len(response["body"]));',
        ],
    },
    "rcon": {
        "title": "rcon",
        "summary": "The Source RCON protocol for game servers.",
        "intro": """
A session is a socket handle, so `rcon.attach` can take over a connection that
was opened elsewhere — through a tunnel, or by a test server. Responses that
arrive in several packets (the 4096-byte split is common) are joined before
`rcon.execute` returns.
""",
        "example": [
            'import "std:rcon";',
            "",
            'let session = rcon.connect("127.0.0.1", 27015, "password");',
            'println(rcon.execute(session, "status"));',
            "rcon.close(session);",
        ],
    },
    "sftp": {
        "title": "sftp",
        "summary": "File transfer over SSH, by driving the OpenSSH client.",
        "intro": """
SFTP is a subsystem inside an SSH connection, and SSH is cryptography end to
end, so rather than link an SSH library — which would stop compiled programs from
being self-contained — `sftp` runs the OpenSSH `sftp` program in batch mode. This
is the only place TERN starts an external program.

The batch runs with `BatchMode=yes`, so a missing key fails immediately instead
of prompting, and `StrictHostKeyChecking=accept-new`, so an unknown host is
accepted once and a changed key is still refused.
""",
        "example": [
            'import "std:sftp";',
            "",
            'let server = {"host": "example.com", "user": "deploy", "key": "/home/me/.ssh/id_ed25519"};',
            'sftp.upload("build/app.tar.gz", "/srv/app.tar.gz", server);',
            'println(sftp.list("/srv", server));',
        ],
    },
    "fs": {
        "title": "fs",
        "summary": "Files, directories and reading a file in pieces.",
        "intro": """
`fs.read` loads a whole file, which is the wrong shape for a log that is still
growing or an archive that is four gigabytes. `fs.open` returns a handle that
keeps its position, `fs.readChunk` returns the next piece, and `fs.readAt` does
the same read without a handle. Chunks are byte-addressed and never split a
UTF-8 character, so they concatenate back into the file.
""",
        "example": [
            'import "std:fs";',
            "",
            'let file = fs.open("/var/log/huge.log");',
            'fs.seek(file, 512 * 1024 * 1024);',
            'let chunk = fs.readChunk(file, 10 * 1024 * 1024);',
            "println(len(chunk), fs.position(file));",
            "fs.close(file);",
        ],
    },
    "gl": {
        "title": "gl",
        "summary": "OpenGL, loaded at run time so nothing is linked.",
        "intro": """
libEGL is opened with `dlopen`/`NativeLibrary` on first use, a surfaceless
pbuffer supplies a context with no window server, and the GL entry points come
from `eglGetProcAddress`. A script that never renders needs no driver, and a
machine without one gets a catchable `GLError`.

The context is OpenGL 3.3 core. Vertices carry `x, y, r, g, b, a`: attribute 0
is the `vec2` position and attribute 1 the `vec4` colour.
""",
        "example": [
            'import "std:gl";',
            "",
            "let surface = gl.create(640, 480);",
            "gl.clear(surface, 0.1, 0.1, 0.15, 1);",
            "let program = gl.program(surface, vertexSource, fragmentSource);",
            "gl.draw(surface, program, [-0.5, -0.5, 1, 0, 0, 1, 0.5, -0.5, 0, 1, 0, 1, 0, 0.5, 0, 0, 1, 1]);",
            'gl.save(surface, "triangle.png");',
            "gl.close(surface);",
        ],
    },
    "d3d12": {
        "title": "d3d12",
        "summary": "Direct3D 12: device, render target, pipeline and draw.",
        "intro": """
`d3d12.dll` is opened at run time and every call goes through the COM vtable at
the slot the Windows SDK gives it. The structures are mirrored by hand, with the
sizes and offsets asserted against the real `d3d12.h` by the Windows build. HLSL
is compiled at run time by `d3dcompiler_47.dll`.

This is an offscreen renderer: surfaces are pbuffer-like targets that can be read
back as pixels and saved as PNG. There is no swap chain and no window.

The pipeline takes HLSL with the entry point `main` in both stages; the vertex
input is `POSITION` (`float3`) followed by `COLOR` (`float4`).
""",
        "example": [
            'import "std:d3d12";',
            "",
            "let target = d3d12.create(640, 480);",
            "d3d12.clear(target, 0.1, 0.1, 0.15, 1);",
            "let pipeline = d3d12.pipeline(target, vertexSource, pixelSource);",
            "d3d12.draw(target, pipeline, [-0.5, -0.5, 1, 0, 0, 1, 0.5, -0.5, 0, 1, 0, 1, 0, 0.5, 0, 0, 1, 1]);",
            'd3d12.save(target, "triangle.png");',
            "d3d12.close(target);",
        ],
    },
}

CONNECTION = (
    "connection",
    "int",
    "A connection handle from `net.connect` or `net.accept`.",
)

FUNCTIONS = {
    # ------------------------------------------------------------------ net
    "net.encode": {
        "summary": "Encodes a value into the wire format without sending it.",
        "params": [("value", "any", "Any value except a function, a native or a type.")],
        "returns": ("list of int", "The encoded bytes, one integer per byte."),
        "errors": [("SerializationError", "The value holds something the format cannot carry: a function, a native, or a cycle.")],
        "remarks": "`net.send` is `encode` plus a four-byte big-endian length and one write. "
                   "Structs carry their type name and field names, so a receiver with the same "
                   "`struct` gets an instance back and a receiver without it gets a plain map.",
        "example": ['let bytes = net.encode({"a": 1});', "println(len(bytes) > 0);"],
        "see": ["net.decode", "net.send"],
    },
    "net.decode": {
        "summary": "Decodes a value from the wire format.",
        "params": [("bytes", "list of int", "Bytes produced by `net.encode`, without the frame header.")],
        "returns": ("any", "The decoded value."),
        "errors": [("TypeError", "The argument is not a list of byte values."),
                   ("SerializationError", "The bytes are truncated, carry an unknown tag, or have bytes left over.")],
        "example": ['println(net.decode(net.encode([1, 2, 3])));'],
        "see": ["net.encode", "net.receive"],
    },
    "net.listen": {
        "summary": "Opens a listening socket and returns a server handle.",
        "params": [("port", "int", "The port to bind; `0` asks the system for a free one. `net.port` reports it."),
                   ("host", "string", "In the three-argument form, the address to bind."),
                   ("options", "map", "Optional: `token`, `maxFrame`, `timeout`.")],
        "returns": ("int", "A server handle for `net.accept`."),
        "errors": [("TypeError", "An option has the wrong type."),
                   ("ArgumentError", "`maxFrame` is outside 256..67108864, `timeout` is negative, or `token` is empty."),
                   ("NetError", "The address cannot be bound.")],
        "remarks": "Three forms are accepted: `listen(port)`, `listen(port, options)` and "
                   "`listen(host, port, options)`; a numeric second argument selects the last. "
                   "`token` makes every accepted peer prove it knows the string before the "
                   "connection exists. `maxFrame` caps the frame a peer may claim (default 1 MiB) "
                   "and is checked before the payload is read, so a hostile header cannot make "
                   "this side allocate.",
        "example": ['let server = net.listen(0, {"token": "s3cret", "maxFrame": 4096});',
                    'println(net.port(server) > 0);'],
        "see": ["net.accept", "net.connect", "net.maxFrame"],
    },
    "net.accept": {
        "summary": "Waits for a peer and returns a connection handle.",
        "params": [("server", "int", "A server handle from `net.listen`.")],
        "returns": ("int", "A connection handle."),
        "errors": [("NetError", "The handle is not a server, or the accept fails."),
                   ("AuthenticationError", "The peer did not present the listener's token.")],
        "remarks": "The hello frame is read and checked here, so a refused peer is reported to "
                   "the caller and never becomes a connection. The peer is told `token rejected` "
                   "first so it fails with a clear message of its own.",
        "example": ["let server = net.listen(0);", "let client = net.connect(\"127.0.0.1\", net.port(server));",
                    "let peer = net.accept(server);"],
        "see": ["net.listen", "net.receive"],
    },
    "net.connect": {
        "summary": "Connects to a listening socket and returns a handle.",
        "params": [("host", "string", "A host name or address."),
                   ("port", "int", "The port."),
                   ("options", "map", "Optional: `token`, `maxFrame`, `timeout`, and for TLS `tls`, `sni`, `verify`, `ca`.")],
        "returns": ("int", "A connection handle."),
        "errors": [("TypeError", "`host` is not a string or `port` is not an integer."),
                   ("ArgumentError", "An option is out of range."),
                   ("NetError", "The address cannot be resolved or the connection is refused."),
                   ("AuthenticationError", "The listener rejected the token."),
                   ("TlsError", "The TLS handshake failed.")],
        "remarks": "`timeout` is milliseconds and covers the connect and the hello exchange. "
                   "`tls: true` wraps the socket before anything is sent; `sni` defaults to the "
                   "host name and is what the certificate is checked against.",
        "example": ['let connection = net.connect("example.com", 443, {"tls": true, "timeout": 5000});'],
        "see": ["net.listen", "net.send", "net.receive"],
    },
    "net.send": {
        "summary": "Encodes a value and sends it as one frame.",
        "params": [CONNECTION, ("value", "any", "The value to send.")],
        "returns": None,
        "errors": [("TypeError", "The handle is not a connection, or the value cannot be encoded."),
                   ("NetError", "The write fails or times out."),
                   ("ArgumentError", "The encoded frame is larger than the peer's ceiling.")],
        "example": ['net.send(connection, {"kind": "ping", "id": 7});'],
        "see": ["net.receive", "net.encode", "net.maxFrame"],
    },
    "net.receive": {
        "summary": "Reads one frame and decodes it.",
        "params": [CONNECTION],
        "returns": ("any", "The decoded value."),
        "errors": [("NetError", "The read fails or the peer closes mid-frame."),
                   ("SerializationError", "The frame is malformed."),
                   ("ArgumentError", "The frame claims more bytes than this side accepts.")],
        "remarks": "A timeout set with `net.timeout` raises `NetError`; the frame ceiling is "
                   "checked from the four-byte header before any payload is allocated.",
        "example": ["let message = net.receive(connection);", 'println(message["kind"]);'],
        "see": ["net.send", "net.timeout", "net.maxFrame"],
    },
    "net.sendBytes": {
        "summary": "Writes raw bytes with no frame header.",
        "params": [CONNECTION, ("bytes", "list of int", "Byte values 0..255.")],
        "returns": None,
        "errors": [("TypeError", "The argument is not a list, or an item is not an integer."),
                   ("ArgumentError", "An item is outside 0..255."),
                   ("NetError", "The write fails or times out.")],
        "remarks": "For protocols layered on top of `net` — HTTP, RCON, a hand-written wire "
                   "format. `net.receiveBytes` is the matching read.",
        "example": ["net.sendBytes(connection, [1, 2, 3, 255]);"],
        "see": ["net.receiveBytes"],
    },
    "net.receiveBytes": {
        "summary": "Reads exactly n raw bytes.",
        "params": [CONNECTION, ("count", "int", "How many bytes to read.")],
        "returns": ("list of int", "The bytes, as integers 0..255."),
        "errors": [("ArgumentError", "`count` is negative or beyond the frame ceiling."),
                   ("NetError", "The peer closes before `count` bytes arrive, or the read times out.")],
        "example": ["println(net.receiveBytes(connection, 4));"],
        "see": ["net.sendBytes"],
    },
    "net.receiveSome": {
        "summary": "Reads up to n bytes, or returns an empty list at end of stream.",
        "params": [CONNECTION, ("count", "int", "The most bytes to take, 1..67108864.")],
        "returns": ("list of int", "What arrived: 1..count bytes, or an empty list at end of stream."),
        "errors": [("NetError", "`count` is out of range, the read times out, or the socket is closed."),
                   ("ConnectionError", "The other side reset the connection.")],
        "remarks": "The difference from `net.receiveBytes` is that a short read is normal: it "
                   "returns as soon as any bytes arrive, so it suits a protocol loop that reads "
                   "until the peer closes. An empty list means end of stream, not a timeout - a "
                   "timeout raises `NetError`, which is why a caller wanting to distinguish the "
                   "two should set `net.timeout` first. Reads do not cross frame headers; use "
                   "this only on a connection opened or accepted with the `raw` option.",
        "example": ["while true {", "    let chunk = net.receiveSome(connection, 4096);",
                    "    if len(chunk) == 0 { break; }", "    println(len(chunk));", "}"],
        "see": ["net.receiveBytes", "net.sendBytes", "net.timeout"],
    },
    "net.maxFrame": {
        "summary": "Reads, or changes, the largest frame a connection accepts.",
        "params": [CONNECTION, ("bytes", "int", "The new ceiling, 256..67108864.")],
        "returns": ("int", "The ceiling now in force."),
        "errors": [("ArgumentError", "The value is out of range.")],
        "remarks": "A connection inherits its server's ceiling. Raising it lets a peer send "
                   "larger frames; the receiver still checks the header before allocating.",
        "example": ["println(net.maxFrame(connection, 65536));"],
        "see": ["net.listen", "net.receive"],
    },
    "net.timeout": {
        "summary": "Sets the read and write timeout of a connection.",
        "params": [CONNECTION, ("milliseconds", "int", "Zero means no timeout.")],
        "returns": None,
        "errors": [("ArgumentError", "The value is negative.")],
        "example": ["net.timeout(connection, 1500);"],
        "see": ["net.receive"],
    },
    "net.close": {
        "summary": "Closes a connection or a server.",
        "params": [("handle", "int", "A connection, server or datagram handle.")],
        "returns": None,
        "errors": [("NetError", "There is no open socket with that handle.")],
        "remarks": "Closing twice is not an error at the socket level, but the handle is gone, "
                   "so a second `net.close` reports the missing handle like any other call would.",
        "example": ["net.close(connection);"],
        "see": ["net.listen", "net.connect"],
    },
    "net.port": {
        "summary": "The port a server is bound to.",
        "params": [("server", "int", "A server handle.")],
        "returns": ("int", "The port number."),
        "errors": [("NetError", "The handle is not an open server.")],
        "remarks": "Useful after `net.listen(0)`, which asks the system for a free port.",
        "example": ["let port = net.port(server);"],
        "see": ["net.listen", "net.peer"],
    },
    "net.peer": {
        "summary": "The remote address of a connection, as `host:port`.",
        "params": [CONNECTION],
        "returns": ("string", "The address."),
        "errors": [("NetError", "The handle is not an open connection.")],
        "example": ['println(net.peer(connection).startsWith("127.0.0.1:"));'],
        "see": ["net.port"],
    },
    "net.udpListen": {
        "summary": "Opens a datagram socket.",
        "params": [("port", "int", "The port to bind; `0` asks for a free one."),
                   ("host", "string", "In the three-argument form, the address to bind."),
                   ("options", "map", "Optional; none are read by datagram sockets.")],
        "returns": ("int", "A datagram handle."),
        "errors": [("ArgumentError", "The port is out of range."),
                   ("NetError", "The socket cannot be opened.")],
        "example": ["let socket = net.udpListen(0);"],
        "see": ["net.udpSend", "net.udpReceive"],
    },
    "net.udpSend": {
        "summary": "Sends one datagram.",
        "params": [("socket", "int", "A datagram handle."),
                   ("host", "string", "Where to send it."),
                   ("port", "int", "The destination port."),
                   ("bytes", "list of int", "The payload, at most 65507 bytes.")],
        "returns": None,
        "errors": [("ArgumentError", "The payload is larger than 65507 bytes."),
                   ("NetError", "The send fails.")],
        "remarks": "Datagrams are not framed and not acknowledged: the payload is exactly the "
                   "bytes given.",
        "example": ['net.udpSend(socket, "127.0.0.1", port, [1, 2, 3]);'],
        "see": ["net.udpReceive"],
    },
    "net.udpReceive": {
        "summary": "Waits for one datagram.",
        "params": [("socket", "int", "A datagram handle."),
                   ("maxBytes", "int", "Optional ceiling; 65507 by default.")],
        "returns": ("map", '`{"from": "host:port", "data": [bytes]}`.'),
        "errors": [("NetError", "The socket fails.")],
        "remarks": "A timeout set with `net.timeout` returns `nil` rather than raising, which "
                   "is what makes a receive loop possible; anything longer than `maxBytes` is "
                   "truncated.",
        "example": ["let packet = net.udpReceive(socket);", 'if (packet != nil) { println(packet["from"]); }'],
        "see": ["net.udpSend", "net.udpListen"],
    },

    # ----------------------------------------------------------------- http
    "http.get": {
        "summary": "Performs a GET and returns the response.",
        "params": [("url", "string", "An `http://` or `https://` URL."),
                   ("options", "map", "Optional: `headers`, `timeout`, `redirects`, `maxBytes`, `verify`, `ca`.")],
        "returns": ("map", "`status`, `reason`, `version`, `headers`, `body`."),
        "errors": [("ArgumentError", "The scheme is not http or https."),
                   ("HttpError", "The response is malformed, too large, or the exchange times out."),
                   ("NetError", "The connection fails.")],
        "example": ['let page = http.get("https://example.com/");', 'println(page["status"]);'],
        "see": ["http.post", "http.request", "http.exchange"],
    },
    "http.post": {
        "summary": "Performs a POST with a body.",
        "params": [("url", "string", "The URL."),
                   ("body", "string", "The request body."),
                   ("options", "map", "As for `http.get`.")],
        "returns": ("map", "The response, as for `http.get`."),
        "errors": [("HttpError", "The exchange fails.")],
        "example": ['let sent = http.post("https://example.com/api", "{\\"ok\\": true}", {"headers": {"content-type": "application/json"}});'],
        "see": ["http.get", "http.request"],
    },
    "http.request": {
        "summary": "Performs a request with any method.",
        "params": [("method", "string", "The HTTP method."),
                   ("url", "string", "The URL."),
                   ("options", "map", "As for `http.get`, plus `body`.")],
        "returns": ("map", "The response."),
        "errors": [("HttpError", "The exchange fails.")],
        "example": ['let removed = http.request("DELETE", "https://example.com/item/1");'],
        "see": ["http.get", "http.post"],
    },
    "http.encodeRequest": {
        "summary": "Renders a request as the bytes that would be sent.",
        "params": [("method", "string", "The method."), ("url", "string", "The URL."),
                   ("options", "map", "As for `http.get`.")],
        "returns": ("list of int", "The request bytes, ready for `net.sendBytes` or `http.exchange`."),
        "errors": [("ArgumentError", "The URL scheme is not supported.")],
        "remarks": "Pure: nothing is sent and no socket is opened, which is what makes the "
                   "header rules testable. The `Host` header carries a port only when it is not "
                   "the scheme's default. `encodeRequest` returns bytes, not text, because a "
                   "body may be arbitrary bytes.",
        "example": ['println(http.encodeRequest("GET", "https://example.com/a"));'],
        "see": ["http.decodeResponse", "http.exchange"],
    },
    "http.decodeResponse": {
        "summary": "Parses a response from bytes.",
        "params": [("bytes", "list of int", "A complete response: headers and body as bytes.")],
        "returns": ("map", "The response, as for `http.get`."),
        "errors": [("HttpError", "The text is not a well-formed response.")],
        "remarks": "Handles chunked bodies and `Content-Length`; the reason phrase may be empty.",
        "example": ['let bytes = [];', 'for (c in "HTTP/1.1 204 No Content".chars()) { bytes.push(ord(c)); }',
                    'bytes.push(13); bytes.push(10); bytes.push(13); bytes.push(10);',
                    'println(http.decodeResponse(bytes)["status"]);'],
        "see": ["http.encodeRequest"],
    },
    "http.exchange": {
        "summary": "Sends a request over a socket the caller already owns.",
        "params": [("connection", "int", "A connection from `net.connect`, already in the right state."),
                   ("method", "string", "The HTTP method."),
                   ("url", "string", "The URL."),
                   ("options", "map", "Optional; as for `http.get`, including `body`.")],
        "returns": ("map", "The response."),
        "errors": [("HttpError", "The response is incomplete."),
                   ("NetError", "The socket fails.")],
        "remarks": "This is how a script talks HTTP over a tunnel, a proxy, or a TLS "
                   "connection it set up itself.",
        "example": ['let reply = http.exchange(connection, "GET", "http://example.com/");'],
        "see": ["http.encodeRequest", "net.connect"],
    },

    # ----------------------------------------------------------------- rcon
    "rcon.connect": {
        "summary": "Opens a session and authenticates.",
        "params": [("host", "string", "The server."), ("port", "int", "The RCON port."),
                   ("password", "string", "The RCON password."),
                   ("options", "map", "Optional: `timeout`, milliseconds, default 5000.")],
        "returns": ("int", "A session handle."),
        "errors": [("AuthenticationError", "The password is refused."),
                   ("RconError", "The connection fails.")],
        "example": ['let session = rcon.connect("127.0.0.1", 27015, "password");'],
        "see": ["rcon.execute", "rcon.attach"],
    },
    "rcon.attach": {
        "summary": "Takes over a connection as an RCON session.",
        "params": [("socket", "int", "A connection from `net.connect`."),
                   ("password", "string", "The RCON password."),
                   ("options", "map", "Optional: `timeout`.")],
        "returns": ("int", "The same handle, now an RCON session."),
        "errors": [("NetError", "The handle is not a plain connection."),
                   ("AuthenticationError", "The password is refused; the socket is left open.")],
        "remarks": "After attaching, `net.send` on the handle reports `needs a connection "
                   "handle, not an rcon session` — the handle belongs to `rcon` now.",
        "example": ['let session = rcon.attach(net.connect("127.0.0.1", 27015), "password");'],
        "see": ["rcon.connect", "rcon.execute"],
    },
    "rcon.execute": {
        "summary": "Runs one command and returns everything the server sent.",
        "params": [("session", "int", "An RCON session."), ("command", "string", "The command line.")],
        "returns": ("string", "The response text, joined across packets."),
        "errors": [("RconError", "The exchange fails or times out.")],
        "example": ['println(rcon.execute(session, "status"));'],
        "see": ["rcon.connect"],
    },
    "rcon.close": {
        "summary": "Closes a session.",
        "params": [("session", "int", "An RCON session.")],
        "returns": None,
        "errors": [("NetError", "The handle is not an RCON session.")],
        "example": ["rcon.close(session);"],
        "see": ["rcon.connect"],
    },

    # ----------------------------------------------------------------- sftp
    "sftp.upload": {
        "summary": "Copies a local file to the server.",
        "params": [("local", "string", "A local path that must exist."),
                   ("remote", "string", "The destination path."),
                   ("options", "map", "`host`, `user`, and optionally `key`, `port`, `timeout`, `binary`.")],
        "returns": None,
        "errors": [("SftpError", "The local file is missing, the key cannot be read, or `sftp` exits non-zero.")],
        "remarks": "The error message carries the first line of `sftp`'s own error output, so "
                   "`Permission denied (publickey)` reaches the script unchanged. The `binary` "
                   "option names the program to run — the string `sftp` by default — which is "
                   "how the tests substitute a stand-in.",
        "example": ['sftp.upload("build/app.tar.gz", "/srv/app.tar.gz", {"host": "example.com", "user": "deploy"});'],
        "see": ["sftp.download", "sftp.list"],
    },
    "sftp.download": {
        "summary": "Copies a file from the server.",
        "params": [("remote", "string", "The source path."), ("local", "string", "The destination path."),
                   ("options", "map", "As for `sftp.upload`.")],
        "returns": None,
        "errors": [("SftpError", "The transfer fails.")],
        "example": ['sftp.download("/var/log/app.log", "app.log", server);'],
        "see": ["sftp.upload"],
    },
    "sftp.list": {
        "summary": "Lists a remote directory.",
        "params": [("remote", "string", "The directory."), ("options", "map", "As for `sftp.upload`.")],
        "returns": ("list of string", "Entry names, one per line of `ls -1`."),
        "errors": [("SftpError", "The listing fails.")],
        "example": ['println(sftp.list("/srv", server));'],
        "see": ["sftp.upload"],
    },
    "sftp.run": {
        "summary": "Runs batch commands and reports the outcome without raising.",
        "params": [("commands", "list of string", "Commands for the `sftp` batch parser."),
                   ("options", "map", "As for `sftp.upload`.")],
        "returns": ("map", "`status` (exit code), `output`, `error`."),
        "errors": [("SftpError", "A command contains a line break.")],
        "remarks": "`upload`, `download` and `list` are built on this; they raise where `run` "
                   "returns. A non-zero status is data here, not an exception.",
        "example": ['let report = sftp.run(["ls -1 /"], server);', 'println(report["status"]);'],
        "see": ["sftp.batch", "sftp.command"],
    },
    "sftp.batch": {
        "summary": "The batch file text for a list of commands.",
        "params": [("commands", "list of string", "One line per command.")],
        "returns": ("string", "The text, newline terminated."),
        "errors": [("TypeError", "The argument is not a list, or an item is not a string.")],
        "remarks": "Pure. Useful for printing exactly what would run.",
        "example": ['print(sftp.batch(["put a.txt b.txt"]));'],
        "see": ["sftp.command", "sftp.run"],
    },
    "sftp.command": {
        "summary": "The command line `sftp` is started with.",
        "params": [("options", "map", "As for `sftp.upload`.")],
        "returns": ("list of string", "The argv, program name first."),
        "errors": [("ArgumentError", "`host` or `user` is missing, or an option is out of range.")],
        "remarks": "`-P` is always passed, `-i` only when `key` is set, and the batch file is "
                   "fed on standard input (`-b -`) so no temporary file is written.",
        "example": ['println(sftp.command({"host": "h", "user": "u"}));'],
        "see": ["sftp.batch"],
    },

    # ------------------------------------------------------------------- fs
    "fs.read": {
        "summary": "Reads a whole file as text.",
        "params": [("path", "string", "The file.")],
        "returns": ("string", "The contents."),
        "errors": [("IOError", "The file cannot be read."), ("TypeError", "The path is not a string."),
                   ("PermissionError", "File system access is disabled for this script.")],
        "remarks": "For a file that may be large, use `fs.open` and `fs.readChunk` instead: "
                   "this one loads everything.",
        "example": ['println(fs.read("notes.txt"));'],
        "see": ["fs.readChunk", "fs.readAt", "fs.readBytes"],
    },
    "fs.readBytes": {
        "summary": "Reads a whole file as a list of bytes.",
        "params": [("path", "string", "The file.")],
        "returns": ("list of int", "The bytes."),
        "errors": [("IOError", "The file cannot be read.")],
        "example": ['println(fs.readBytes("logo.png")[0..4]);'],
        "see": ["fs.read", "fs.readAt"],
    },
    "fs.write": {
        "summary": "Writes text, replacing the file and creating its directories.",
        "params": [("path", "string", "The file."), ("text", "any", "Rendered with `str`.")],
        "returns": None,
        "errors": [("IOError", "The file cannot be written.")],
        "example": ['fs.write("out/report.txt", "done");'],
        "see": ["fs.append"],
    },
    "fs.append": {
        "summary": "Appends text, creating the file if it is missing.",
        "params": [("path", "string", "The file."), ("text", "any", "Rendered with `str`.")],
        "returns": None,
        "errors": [("IOError", "The file cannot be written.")],
        "example": ['fs.append("log.txt", "one line\\n");'],
        "see": ["fs.write"],
    },
    "fs.exists": {
        "summary": "True when a file or directory exists.",
        "params": [("path", "string", "The path.")],
        "returns": ("bool", "Whether it exists."),
        "example": ['println(fs.exists("build"));'],
        "see": ["fs.isFile", "fs.isDir"],
    },
    "fs.size": {
        "summary": "Size of a file in bytes.",
        "params": [("path", "string", "The file.")],
        "returns": ("int", "The size."),
        "errors": [("IOError", "The file cannot be statted.")],
        "example": ['println(fs.size("app.tar.gz"));'],
        "see": ["fs.fileSize", "fs.readAt"],
    },
    "fs.remove": {
        "summary": "Deletes a file or a directory tree.",
        "params": [("path", "string", "The path; a missing path is not an error.")],
        "returns": None,
        "example": ['fs.remove("build");'],
        "see": ["fs.mkdir"],
    },
    "fs.list": {
        "summary": "Directory entries as a list of names.",
        "params": [("path", "string", "The directory.")],
        "returns": ("list of string", "Names, sorted."),
        "errors": [("IOError", "The directory cannot be read.")],
        "example": ['println(fs.list("tests"));'],
        "see": ["fs.isDir"],
    },
    "fs.mkdir": {
        "summary": "Creates a directory and its parents.",
        "params": [("path", "string", "The directory.")],
        "returns": None,
        "errors": [("IOError", "The directory cannot be created.")],
        "example": ['fs.mkdir("build/out");'],
        "see": ["fs.remove"],
    },
    "fs.isFile": {
        "summary": "True for a regular file.",
        "params": [("path", "string", "The path.")],
        "returns": ("bool", "Whether it is a regular file."),
        "example": ['println(fs.isFile("README.md"));'],
        "see": ["fs.isDir"],
    },
    "fs.isDir": {
        "summary": "True for a directory.",
        "params": [("path", "string", "The path.")],
        "returns": ("bool", "Whether it is a directory."),
        "example": ['println(fs.isDir("docs"));'],
        "see": ["fs.isFile"],
    },
    "fs.open": {
        "summary": "Opens a file for reading and returns a handle that keeps its position.",
        "params": [("path", "string", "The file.")],
        "returns": ("int", "A handle for `fs.readChunk`, `fs.seek`, `fs.position`, `fs.fileSize` and `fs.close`."),
        "errors": [("IOError", "The file cannot be opened, or 64 handles are already open.")],
        "remarks": "The handle is independent of the file's size: opening a four-gigabyte "
                   "archive costs one call and allocates nothing until you read.",
        "example": ['let file = fs.open("huge.log");'],
        "see": ["fs.readChunk", "fs.readAt", "fs.close"],
    },
    "fs.readChunk": {
        "summary": "Reads up to n bytes from a handle's position and advances it.",
        "params": [("handle", "int", "A handle from `fs.open`."),
                   ("length", "int", "How many bytes to ask for, 1..1073741824.")],
        "returns": ("string", "The bytes read; fewer than asked at the end of the file, and `\"\"` at EOF."),
        "errors": [("IOError", "The handle is not open."),
                   ("ArgumentError", "`length` is out of range.")],
        "remarks": "A chunk that would end inside a multi-byte character is extended by the two "
                   "or three bytes the character still needs, so chunks concatenate back into the "
                   "file and nothing is decoded twice. Reading at EOF returns `\"\"` rather than "
                   "raising, which is what ends a streaming loop.",
        "example": ['let file = fs.open("huge.log");',
                    'let chunk = fs.readChunk(file, 4096);',
                    'while (chunk != "") { process(chunk); chunk = fs.readChunk(file, 4096); }'],
        "see": ["fs.readAt", "fs.position", "fs.seek"],
    },
    "fs.readAt": {
        "summary": "Reads up to n bytes at a byte offset, without a handle.",
        "params": [("path", "string", "The file."),
                   ("offset", "int", "The byte offset to start at."),
                   ("length", "int", "Optional; the rest of the file by default, capped at 1 GiB.")],
        "returns": ("string", "The bytes read, `\"\"` past the end of the file."),
        "errors": [("IOError", "The file cannot be opened."),
                   ("ArgumentError", "The offset is negative or the length is out of range.")],
        "remarks": "Random access: reading the header of a large archive costs one seek and the "
                   "bytes actually returned.",
        "example": ['println(fs.readAt("huge.log", 512 * 1024 * 1024, 100));'],
        "see": ["fs.readChunk", "fs.seek"],
    },
    "fs.seek": {
        "summary": "Moves a handle's position and returns it.",
        "params": [("handle", "int", "A handle from `fs.open`."), ("offset", "int", "The new byte offset.")],
        "returns": ("int", "The position after seeking, which is the offset."),
        "errors": [("ArgumentError", "The offset is negative.")],
        "remarks": "Seeking past the end is allowed; the next read returns `\"\"`.",
        "example": ["fs.seek(file, 0);"],
        "see": ["fs.position", "fs.readChunk"],
    },
    "fs.position": {
        "summary": "The byte offset the next read starts at.",
        "params": [("handle", "int", "A handle from `fs.open`.")],
        "returns": ("int", "The position."),
        "errors": [("IOError", "The handle is not open.")],
        "remarks": "Save this if you need to resume reading later: it is where the reader got to.",
        "example": ["let resumeFrom = fs.position(file);"],
        "see": ["fs.seek", "fs.readChunk"],
    },
    "fs.fileSize": {
        "summary": "Size of an open file in bytes.",
        "params": [("handle", "int", "A handle from `fs.open`.")],
        "returns": ("int", "The size."),
        "errors": [("IOError", "The handle is not open.")],
        "example": ["println(fs.fileSize(file));"],
        "see": ["fs.size"],
    },
    "fs.close": {
        "summary": "Closes a file handle.",
        "params": [("handle", "int", "A handle from `fs.open`.")],
        "returns": None,
        "errors": [("IOError", "The handle is not open, including a second close.")],
        "example": ["fs.close(file);"],
        "see": ["fs.open"],
    },

    # ------------------------------------------------------------------- gl
    "gl.available": {
        "summary": "True when libEGL could be loaded.",
        "returns": ("bool", "Whether a context can be asked for."),
        "example": ["println(gl.available());"],
        "see": ["gl.info"],
    },
    "gl.info": {
        "summary": "Describes the driver.",
        "returns": ("map", "`vendor`, `renderer`, `version`, `glsl`."),
        "errors": [("GLError", "No driver is available.")],
        "example": ['println(gl.info()["renderer"]);'],
        "see": ["gl.available"],
    },
    "gl.create": {
        "summary": "Creates an offscreen context and returns a handle.",
        "params": [("width", "int", "1..8192."), ("height", "int", "1..8192.")],
        "returns": ("int", "A context handle."),
        "errors": [("ArgumentError", "A dimension is out of range."),
                   ("GLError", "No driver, no config, or the context cannot be made current.")],
        "example": ["let surface = gl.create(640, 480);"],
        "see": ["gl.close", "gl.size"],
    },
    "gl.clear": {
        "summary": "Fills the surface with a colour.",
        "params": [("context", "int", "A context handle."),
                   ("red", "number", "0..1."), ("green", "number", "0..1."),
                   ("blue", "number", "0..1."), ("alpha", "number", "0..1.")],
        "returns": None,
        "errors": [("ArgumentError", "A component is outside 0..1.")],
        "example": ["gl.clear(surface, 0.1, 0.1, 0.15, 1);"],
        "see": ["gl.draw", "gl.pixels"],
    },
    "gl.program": {
        "summary": "Compiles and links a shader program.",
        "params": [("context", "int", "A context handle."),
                   ("vertex", "string", "GLSL for the vertex stage."),
                   ("fragment", "string", "GLSL for the fragment stage.")],
        "returns": ("int", "A program handle."),
        "errors": [("GLError", 'The driver\'s compile log, prefixed with `the vertex shader did not compile: `.')],
        "example": ["let program = gl.program(surface, vertexSource, fragmentSource);"],
        "see": ["gl.draw"],
    },
    "gl.draw": {
        "summary": "Draws triangles.",
        "params": [("context", "int", "A context handle."), ("program", "int", "A program handle."),
                   ("vertices", "list of number", "Six floats per vertex: `x, y, r, g, b, a`, three per triangle.")],
        "returns": None,
        "errors": [("ArgumentError", "The vertex count is not a multiple of six."),
                   ("TypeError", "An element is not a number.")],
        "example": ["gl.draw(surface, program, [-0.5, -0.5, 1, 0, 0, 1, 0.5, -0.5, 0, 1, 0, 1, 0, 0.5, 0, 0, 1, 1]);"],
        "see": ["gl.program", "gl.pixels"],
    },
    "gl.pixels": {
        "summary": "Reads the surface back as RGBA bytes.",
        "params": [("context", "int", "A context handle.")],
        "returns": ("list of int", "`width * height * 4` bytes, bottom row first, like `glReadPixels`."),
        "example": ["println(gl.pixels(surface)[0..4]);"],
        "see": ["gl.save"],
    },
    "gl.save": {
        "summary": "Saves the surface as a PNG.",
        "params": [("context", "int", "A context handle."), ("path", "string", "Where to write it.")],
        "returns": ("int", "The number of bytes written."),
        "errors": [("IOError", "The file cannot be written.")],
        "remarks": "The PNG is written with stored deflate blocks: valid, larger on disk, and "
                   "no zlib in sight.",
        "example": ['gl.save(surface, "frame.png");'],
        "see": ["gl.pixels"],
    },
    "gl.size": {
        "summary": "The size of a context.",
        "params": [("context", "int", "A context handle.")],
        "returns": ("list of int", "`[width, height]`."),
        "example": ["println(gl.size(surface));"],
        "see": ["gl.create"],
    },
    "gl.close": {
        "summary": "Destroys a context and its programs.",
        "params": [("context", "int", "A context handle.")],
        "returns": None,
        "errors": [("GLError", "There is no open context with that handle.")],
        "example": ["gl.close(surface);"],
        "see": ["gl.create"],
    },

    # ---------------------------------------------------------------- d3d12
    "d3d12.available": {
        "summary": "True when the Direct3D 12 library could be loaded.",
        "returns": ("bool", "Whether a device can be asked for."),
        "example": ["println(d3d12.available());"],
        "see": ["d3d12.info"],
    },
    "d3d12.debug": {
        "summary": "Turns the Direct3D 12 debug layer on, and says whether it is running.",
        "params": [("enabled", "bool", "Optional. `true` turns validation on and `false` turns "
                                        "it off; omit it to ask without changing anything.")],
        "returns": ("bool", "Whether validation is on. `false` means this machine has no "
                             "debug layer (Windows Settings > Apps > Optional features > "
                             "Graphics Tools), and the reason is printed once to stderr."),
        "remarks": " ".join([
            "The layer has to be on before the device is created: `d3d12.debug(true)` then "
            "`d3d12.create`, in that order. It makes the driver print exactly which call was "
            "illegal and why, which turns \"the device was removed for an unknown reason\" into "
            "something that can be fixed.",
            "It is off by default because it slows rendering down and is not installed on every "
            "machine.",
        ]),
        "errors": [("TypeError", "The argument is not `true` or `false`.")],
        "example": [
            'd3d12.debug(true);',
            "let target = d3d12.create(640, 480);",
        ],
        "see": ["d3d12.create", "d3d12.info"],
    },
    "d3d12.info": {
        "summary": "Describes the adapter and the binding.",
        "returns": ("map", "`name`, `nodes` and `build`; `build` is the revision of the "
                          "Direct3D 12 binding, which a failure report should quote."),
        "errors": [("D3D12Error", "The library is not available.")],
        "example": ['println(d3d12.info()["name"]);'],
        "see": ["d3d12.available"],
    },
    "d3d12.create": {
        "summary": "Creates a device and an offscreen render target.",
        "params": [("width", "int", "1..16384."), ("height", "int", "1..16384.")],
        "returns": ("int", "A device handle."),
        "errors": [("ArgumentError", "A dimension is out of range."),
                   ("D3D12Error", "The device, target or readback buffer cannot be created.")],
        "example": ["let target = d3d12.create(640, 480);"],
        "see": ["d3d12.close", "d3d12.size"],
    },
    "d3d12.clear": {
        "summary": "Clears the render target.",
        "params": [("device", "int", "A device handle."),
                   ("red", "number", "0..1."), ("green", "number", "0..1."),
                   ("blue", "number", "0..1."), ("alpha", "number", "0..1.")],
        "returns": None,
        "errors": [("ArgumentError", "A component is outside 0..1.")],
        "remarks": "Records a barrier from whatever state the target is in to the render target "
                   "state, a clear, and a barrier back to copy source; the call returns once the "
                   "GPU has finished.",
        "example": ["d3d12.clear(target, 0.1, 0.1, 0.15, 1);"],
        "see": ["d3d12.draw", "d3d12.pixels"],
    },
    "d3d12.pipeline": {
        "summary": "Compiles HLSL and builds a pipeline state.",
        "params": [("device", "int", "A device handle."),
                   ("vertex", "string", "HLSL for the vertex stage; the entry point is `main`."),
                   ("pixel", "string", "HLSL for the pixel stage.")],
        "returns": ("int", "A pipeline handle."),
        "errors": [("D3D12Error", "The compiler is missing, or names the first error in the shader.")],
        "remarks": "Also creates the (empty) root signature. The input layout is fixed: "
                   "`POSITION` as `float3` at offset 0 and `COLOR` as `float4` at offset 12.",
        "example": ["let pipeline = d3d12.pipeline(target, vertexSource, pixelSource);"],
        "see": ["d3d12.draw"],
    },
    "d3d12.draw": {
        "summary": "Draws triangles.",
        "params": [("device", "int", "A device handle."), ("pipeline", "int", "A pipeline handle."),
                   ("vertices", "list of number", "Six floats per vertex: `x, y, r, g, b, a`.")],
        "returns": None,
        "errors": [("ArgumentError", "The vertex count is not a multiple of six."),
                   ("D3D12Error", "The vertex buffer cannot be created or the pipeline is not open.")],
        "remarks": "Each call uploads into a fresh upload-heap buffer: correct and simple. A "
                   "renderer that draws thousands of times a frame would rotate buffers instead.",
        "example": ["d3d12.draw(target, pipeline, [-0.5, -0.5, 1, 0, 0, 1, 0.5, -0.5, 0, 1, 0, 1, 0, 0.5, 0, 0, 1, 1]);"],
        "see": ["d3d12.pipeline", "d3d12.pixels"],
    },
    "d3d12.pixels": {
        "summary": "Reads the target back as RGBA bytes.",
        "params": [("device", "int", "A device handle.")],
        "returns": ("list of int", "`width * height * 4` bytes, bottom row first, like `gl.pixels`."),
        "errors": [("D3D12Error", "The readback buffer cannot be mapped.")],
        "remarks": "Leaves the target in the copy-source state, ready to be read again, so a "
                   "second call in a row needs no barrier of its own.",
        "example": ["println(d3d12.pixels(target)[0..4]);"],
        "see": ["d3d12.save"],
    },
    "d3d12.save": {
        "summary": "Saves the target as a PNG.",
        "params": [("device", "int", "A device handle."), ("path", "string", "Where to write it.")],
        "returns": ("int", "The number of bytes written."),
        "errors": [("IOError", "The file cannot be written.")],
        "example": [
            "let device = d3d12.create(640, 480);",
            "d3d12.draw(device);",
            'println("saved", d3d12.save(device, "frame.png"), "bytes");',
        ],
        "remarks": "The byte count is the writer's own evidence that the picture reached the "
                   "file. A PNG of a 640x480 frame is a few hundred kilobytes, so any other "
                   "number is worth looking at. `d3d12.pixels` returns the same image as a "
                   "list of ints if you want to look at it without a file.",
        "see": ["d3d12.pixels"],
    },
    "d3d12.size": {
        "summary": "The size of a device's target.",
        "params": [("device", "int", "A device handle.")],
        "returns": ("list of int", "`[width, height]`."),
        "example": ["println(d3d12.size(target));"],
        "see": ["d3d12.create"],
    },
    "d3d12.close": {
        "summary": "Releases the device and everything it owns.",
        "params": [("device", "int", "A device handle.")],
        "returns": None,
        "errors": [("D3D12Error", "There is no open device with that handle.")],
        "example": ["d3d12.close(target);"],
        "see": ["d3d12.create"],
    },
    "d3d12.window": {
        "summary": "Opens a window to show frames in.",
        "params": [("width", "int", "1..16384; the client area, in pixels."),
                   ("height", "int", "1..16384."),
                   ("title", "string", "The text in the title bar.")],
        "returns": ("int?", "A window handle, or `nil` when this machine has no window layer."),
        "errors": [("ArgumentError", "A dimension is out of range."),
                   ("TypeError", "The title is not a string.")],
        "remarks": "Win32, loaded at run time. `nil` rather than an error is deliberate: a "
                   "headless machine can still use the rest of this module, and a program that "
                   "tries a window and falls back to files should not need a `try`. Windows only "
                   "for now; on a system without `user32` it always answers `nil`. "
                   "`TERN_USER32_LIBRARY`, `TERN_GDI32_LIBRARY` and `TERN_KERNEL32_LIBRARY` "
                   "override the libraries, which is how the tests drive it.",
        "example": ["let window = d3d12.window(640, 480, \"rotating cube\");"],
        "see": ["d3d12.present", "d3d12.closeWindow"],
    },
    "d3d12.present": {
        "summary": "Shows the target in the window.",
        "params": [("window", "int", "A window handle."),
                   ("device", "int", "A device handle whose target has been drawn to.")],
        "returns": ("bool", "`false` once the window has been closed."),
        "errors": [("D3D12Error", "There is no open window or device with that handle, or the "
                                  "readback buffer cannot be mapped.")],
        "remarks": "Copies the target back, blits it into the window, processes the window's "
                   "messages and paces the frame at about 60 per second. Escape or the close "
                   "button ends the loop that calls it. This is not a DXGI swap chain: a frame "
                   "round-trips through the readback buffer, which is slower than presenting "
                   "properly but needs no presentation surface. `present` is also the call that "
                   "keeps the window responsive, so a loop that stops calling it stops for good.",
        "example": [
            "let window = d3d12.window(640, 480, \"cube\");",
            "while (d3d12.present(window, target)) {",
            "    d3d12.clear(target, 0.05, 0.06, 0.09, 1);",
            "}",
        ],
        "see": ["d3d12.window", "d3d12.pixels"],
    },
    "d3d12.closeWindow": {
        "summary": "Closes a window.",
        "params": [("window", "int", "A window handle.")],
        "returns": None,
        "errors": [("D3D12Error", "There is no open window with that handle.")],
        "remarks": "Destroys the window and forgets the handle. A window that the user has "
                   "already closed still has to be released this way, or its slot stays taken.",
        "example": ["d3d12.closeWindow(window);"],
        "see": ["d3d12.window"],
    },
}
