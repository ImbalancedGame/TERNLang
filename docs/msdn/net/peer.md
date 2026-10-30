# net.peer

The remote address of a connection, as `host:port`.

## Syntax

```tern
net.peer(connection)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `connection` | `int` | A connection handle from `net.connect` or `net.accept`. |

## Return value

- Type: `string`
- The address.

## Errors

| Error | Raised when |
| --- | --- |
| `NetError` | The handle is not an open connection. |

## Example

```tern
println(net.peer(connection).startsWith("127.0.0.1:"));
```

## See also

- [net.port](port.md)

---

Part of the [`net`](index.md) namespace.
