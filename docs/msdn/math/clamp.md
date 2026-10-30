# math.clamp

Constrains a value to a range.

## Syntax

```tern
math.clamp(value, low, high)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `number` | The value. |
| `low` | `number` | The lower bound. |
| `high` | `number` | The upper bound. |

## Return value

- Type: `number`
- The value, or the nearer bound.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | `low` is greater than `high`. |

## Example

```tern
println(math.clamp(15, 0, 10), math.clamp(-1, 0, 10));
```

## See also

- [math.min](min.md)
- [math.max](max.md)

---

Part of the [`math`](index.md) namespace.
