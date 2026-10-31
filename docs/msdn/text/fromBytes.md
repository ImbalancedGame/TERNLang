# text.fromBytes

Decodes a list of bytes as UTF-8 text.

## Syntax

```tern
text.fromBytes(bytes, offset?, length?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `bytes` | `list of int` | The bytes, as integers 0..255. |
| `offset` | `int` | Optional, where to start; 0 by default. |
| `length` | `int` | Optional, how many bytes; the rest of the list by default. |

## Return value

- Type: `string`
- The decoded text.

## Remarks

Invalid UTF-8 decodes to U+FFFD rather than raising: a sequence split across two reads should give a replacement character, not kill the reader. The offset and length let a caller decode one field out of a packet without copying the bytes first.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The first argument is not a list, or an item is not an integer. |
| `ArgumentError` | The offset and length fall outside the list. |

## Example

```tern
println(text.fromBytes([104, 195, 169, 108, 108, 111]));
```

## See also

- [text.toBytes](toBytes.md)
- [net.receiveSome](../net/receiveSome.md)

---

Part of the [`text`](index.md) namespace.
