# net.receiveBytes

Reads exactly n raw bytes.

## Syntax

```tern
net.receiveBytes(connection, count)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `connection` | `int` | A connection handle from `net.connect` or `net.accept`. |
| `count` | `int` | How many bytes to read. |

## Return value

- Type: `list of int`
- The bytes, as integers 0..255.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | `count` is negative or beyond the frame ceiling. |
| `NetError` | The peer closes before `count` bytes arrive, or the read times out. |

## Example

```tern
println(net.receiveBytes(connection, 4));
```

## See also

- [net.sendBytes](sendBytes.md)

---

Part of the [`net`](index.md) namespace.
