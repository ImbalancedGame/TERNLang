# int.clamp

Constrains the value to a range.

## Syntax

```tern
int.clamp(low, high)
```

`receiver` is the int on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `low` | `int` | The lower bound. |
| `high` | `int` | The upper bound. |

## Return value

- Type: `int`
- The value, or the nearer bound.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | `low` is greater than `high`. |

## Example

```tern
println(15.clamp(0, 10));
```

## See also

- [math.clamp](../math/clamp.md)

---

Part of the [`int` methods](index.md).
