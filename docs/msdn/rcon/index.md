# rcon

The Source RCON protocol for game servers.

A session is a socket handle, so `rcon.attach` can take over a connection that
was opened elsewhere — through a tunnel, or by a test server. Responses that
arrive in several packets (the 4096-byte split is common) are joined before
`rcon.execute` returns.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`attach`](attach.md) | 2..3 | Takes over a connection as an RCON session. |
| [`close`](close.md) | 1..1 | Closes a session. |
| [`connect`](connect.md) | 3..4 | Opens a session and authenticates. |
| [`execute`](execute.md) | 2..2 | Runs one command and returns everything the server sent. |

## Example

```tern
import "std:rcon";

let session = rcon.connect("127.0.0.1", 27015, "password");
println(rcon.execute(session, "status"));
rcon.close(session);
```

