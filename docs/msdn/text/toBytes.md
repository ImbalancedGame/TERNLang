# text.toBytes

Encodes text as UTF-8 bytes.

## Syntax

```tern
text.toBytes(text)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `text` | `string` | The text to encode. |

## Return value

- Type: `list of int`
- The UTF-8 bytes, as integers 0..255.

## Remarks

The inverse of `text.fromBytes`. Everything the runtime reads and writes - files, sockets, `net.sendBytes` - is bytes, so this is how text crosses into a byte-level protocol.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The argument is not a string. |

## Example

```tern
println(text.toBytes("héllo"));
```

## See also

- [text.fromBytes](fromBytes.md)
- [net.sendBytes](../net/sendBytes.md)

---

Part of the [`text`](index.md) namespace.
