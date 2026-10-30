# net.encode

Encodes a value into the wire format without sending it.

## Syntax

```tern
net.encode(value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `any` | Any value except a function, a native or a type. |

## Return value

- Type: `list of int`
- The encoded bytes, one integer per byte.

## Remarks

`net.send` is `encode` plus a four-byte big-endian length and one write. Structs carry their type name and field names, so a receiver with the same `struct` gets an instance back and a receiver without it gets a plain map.

## Errors

| Error | Raised when |
| --- | --- |
| `SerializationError` | The value holds something the format cannot carry: a function, a native, or a cycle. |

## Example

```tern
let bytes = net.encode({"a": 1});
println(len(bytes) > 0);
```

## See also

- [net.decode](decode.md)
- [net.send](send.md)

---

Part of the [`net`](index.md) namespace.
