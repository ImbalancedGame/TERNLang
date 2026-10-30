# net.sendBytes

Writes raw bytes with no frame header.

## Syntax

```tern
net.sendBytes(connection, bytes)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `connection` | `int` | A connection handle from `net.connect` or `net.accept`. |
| `bytes` | `list of int` | Byte values 0..255. |

## Return value

`nil`.

## Remarks

For protocols layered on top of `net` — HTTP, RCON, a hand-written wire format. `net.receiveBytes` is the matching read.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The argument is not a list, or an item is not an integer. |
| `ArgumentError` | An item is outside 0..255. |
| `NetError` | The write fails or times out. |

## Example

```tern
net.sendBytes(connection, [1, 2, 3, 255]);
```

## See also

- [net.receiveBytes](receiveBytes.md)

---

Part of the [`net`](index.md) namespace.
