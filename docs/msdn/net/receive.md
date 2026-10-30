# net.receive

Reads one frame and decodes it.

## Syntax

```tern
net.receive(connection)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `connection` | `int` | A connection handle from `net.connect` or `net.accept`. |

## Return value

- Type: `any`
- The decoded value.

## Remarks

A timeout set with `net.timeout` raises `NetError`; the frame ceiling is checked from the four-byte header before any payload is allocated.

## Errors

| Error | Raised when |
| --- | --- |
| `NetError` | The read fails or the peer closes mid-frame. |
| `SerializationError` | The frame is malformed. |
| `ArgumentError` | The frame claims more bytes than this side accepts. |

## Example

```tern
let message = net.receive(connection);
println(message["kind"]);
```

## See also

- [net.send](send.md)
- [net.timeout](timeout.md)
- [net.maxFrame](maxFrame.md)

---

Part of the [`net`](index.md) namespace.
