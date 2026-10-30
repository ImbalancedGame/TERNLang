# math.round

Rounds to the nearest integer, or to n digits.

## Syntax

```tern
math.round(value, digits?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `number` | The value. |
| `digits` | `int` | Optional; when given the result is a float. |

## Return value

- Type: `float`
- The rounded value, as a float: `3.0`.

## Remarks

Halves round away from zero, so `round(2.5)` is `3.0` and `round(-2.5)` is `-3.0`. Use `int()` on the result when an integer is wanted.

## Example

```tern
println(math.round(2.5), math.round(2.567, 2));
```

## See also

- [math.floor](floor.md)
- [float.round](../float/round.md)

---

Part of the [`math`](index.md) namespace.
