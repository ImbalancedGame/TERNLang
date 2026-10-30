# net.send

Encodes a value and sends it as one frame.

## Syntax

```tern
net.send(connection, value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `connection` | `int` | A connection handle from `net.connect` or `net.accept`. |
| `value` | `any` | The value to send. |

## Return value

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The handle is not a connection, or the value cannot be encoded. |
| `NetError` | The write fails or times out. |
| `ArgumentError` | The encoded frame is larger than the peer's ceiling. |

## Example

```tern
net.send(connection, {"kind": "ping", "id": 7});
```

## See also

- [net.receive](receive.md)
- [net.encode](encode.md)
- [net.maxFrame](maxFrame.md)

---

Part of the [`net`](index.md) namespace.
