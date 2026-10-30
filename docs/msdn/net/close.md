# net.close

Closes a connection or a server.

## Syntax

```tern
net.close(handle)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `handle` | `int` | A connection, server or datagram handle. |

## Return value

`nil`.

## Remarks

Closing twice is not an error at the socket level, but the handle is gone, so a second `net.close` reports the missing handle like any other call would.

## Errors

| Error | Raised when |
| --- | --- |
| `NetError` | There is no open socket with that handle. |

## Example

```tern
net.close(connection);
```

## See also

- [net.listen](listen.md)
- [net.connect](connect.md)

---

Part of the [`net`](index.md) namespace.
