# float.clamp

Constrains the value to a range.

## Syntax

```tern
float.clamp(low, high)
```

`receiver` is the float on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `low` | `number` | The lower bound. |
| `high` | `number` | The upper bound. |

## Return value

- Type: `float`
- The value, or the nearer bound.

## Example

```tern
println(2.5.clamp(0.0, 1.0));
```

## See also

- [math.clamp](../math/clamp.md)

---

Part of the [`float` methods](index.md).
