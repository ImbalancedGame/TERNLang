# net.decode

Decodes a value from the wire format.

## Syntax

```tern
net.decode(bytes)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `bytes` | `list of int` | Bytes produced by `net.encode`, without the frame header. |

## Return value

- Type: `any`
- The decoded value.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The argument is not a list of byte values. |
| `SerializationError` | The bytes are truncated, carry an unknown tag, or have bytes left over. |

## Example

```tern
println(net.decode(net.encode([1, 2, 3])));
```

## See also

- [net.encode](encode.md)
- [net.receive](receive.md)

---

Part of the [`net`](index.md) namespace.
