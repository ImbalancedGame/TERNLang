# net.accept

Waits for a peer and returns a connection handle.

## Syntax

```tern
net.accept(server)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `server` | `int` | A server handle from `net.listen`. |

## Return value

- Type: `int`
- A connection handle.

## Remarks

The hello frame is read and checked here, so a refused peer is reported to the caller and never becomes a connection. The peer is told `token rejected` first so it fails with a clear message of its own.

## Errors

| Error | Raised when |
| --- | --- |
| `NetError` | The handle is not a server, or the accept fails. |
| `AuthenticationError` | The peer did not present the listener's token. |

## Example

```tern
let server = net.listen(0);
let client = net.connect("127.0.0.1", net.port(server));
let peer = net.accept(server);
```

## See also

- [net.listen](listen.md)
- [net.receive](receive.md)

---

Part of the [`net`](index.md) namespace.
