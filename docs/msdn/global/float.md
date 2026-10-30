# float

Converts a value to a float.

## Syntax

```tern
float(value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `any` | The value. |

## Return value

- Type: `float`
- The value as a float.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | A string cannot be parsed. |
| `TypeError` | The value cannot be converted. |

## Example

```tern
println(float("2.5") + 1.0);
```

## See also

- [int](int.md)
- [string.toFloat](../string/toFloat.md)

---

Part of the [`global`](index.md) namespace.
