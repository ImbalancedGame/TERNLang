# net

Sockets, framing and datagrams.

`net` sends values, not bytes. `net.send` encodes any TERN value — nil, a
number, a string, a list, a map, a struct, a range — and writes it as one frame;
`net.receive` reads a frame and decodes it. The same wire format is used by the
interpreter and by compiled binaries, so the two can talk to each other.

Every connection opens with a hello frame (see `net.listen`), which carries the
protocol version, the frame ceiling and, optionally, a token. A listener can
therefore refuse a stranger before the script ever sees a connection.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`accept`](accept.md) | 1..1 | Waits for a peer and returns a connection handle. |
| [`close`](close.md) | 1..1 | Closes a connection or a server. |
| [`connect`](connect.md) | 2..3 | Connects to a listening socket and returns a handle. |
| [`decode`](decode.md) | 1..1 | Decodes a value from the wire format. |
| [`encode`](encode.md) | 1..1 | Encodes a value into the wire format without sending it. |
| [`listen`](listen.md) | 1..3 | Opens a listening socket and returns a server handle. |
| [`maxFrame`](maxFrame.md) | 2..2 | Reads, or changes, the largest frame a connection accepts. |
| [`peer`](peer.md) | 1..1 | The remote address of a connection, as `host:port`. |
| [`port`](port.md) | 1..1 | The port a server is bound to. |
| [`receive`](receive.md) | 1..1 | Reads one frame and decodes it. |
| [`receiveBytes`](receiveBytes.md) | 2..2 | Reads exactly n raw bytes. |
| [`receiveSome`](receiveSome.md) | 2..2 | Reads up to n bytes, or returns an empty list at end of stream. |
| [`send`](send.md) | 2..2 | Encodes a value and sends it as one frame. |
| [`sendBytes`](sendBytes.md) | 2..2 | Writes raw bytes with no frame header. |
| [`timeout`](timeout.md) | 2..2 | Sets the read and write timeout of a connection. |
| [`udpListen`](udpListen.md) | 1..3 | Opens a datagram socket. |
| [`udpReceive`](udpReceive.md) | 1..2 | Waits for one datagram. |
| [`udpSend`](udpSend.md) | 4..4 | Sends one datagram. |

## Example

```tern
import "std:net";

let server = net.listen(0, {"token": "s3cret"});
let port = net.port(server);

let client = net.connect("127.0.0.1", port, {"token": "s3cret", "timeout": 2000});
let peer = net.accept(server);
net.send(client, {"kind": "ping"});
println(net.receive(peer));
```

