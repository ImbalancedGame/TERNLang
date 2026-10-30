# net.port

The port a server is bound to.

## Syntax

```tern
net.port(server)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `server` | `int` | A server handle. |

## Return value

- Type: `int`
- The port number.

## Remarks

Useful after `net.listen(0)`, which asks the system for a free port.

## Errors

| Error | Raised when |
| --- | --- |
| `NetError` | The handle is not an open server. |

## Example

```tern
let port = net.port(server);
```

## See also

- [net.listen](listen.md)
- [net.peer](peer.md)

---

Part of the [`net`](index.md) namespace.
