# chr

The string for a Unicode code point.

## Syntax

```tern
chr(code)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `code` | `int` | 0..1114111. |

## Return value

- Type: `string`
- The one-character string.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The code point is out of range or a surrogate. |

## Example

```tern
println(chr(9731));
```

## See also

- [ord](ord.md)

---

Part of the [`global`](index.md) namespace.
