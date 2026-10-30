# net.timeout

Sets the read and write timeout of a connection.

## Syntax

```tern
net.timeout(connection, milliseconds)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `connection` | `int` | A connection handle from `net.connect` or `net.accept`. |
| `milliseconds` | `int` | Zero means no timeout. |

## Return value

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The value is negative. |

## Example

```tern
net.timeout(connection, 1500);
```

## See also

- [net.receive](receive.md)

---

Part of the [`net`](index.md) namespace.
