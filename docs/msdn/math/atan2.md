# math.atan2

The angle of a vector.

## Syntax

```tern
math.atan2(y, x)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `y` | `number` | The vertical component. |
| `x` | `number` | The horizontal component. |

## Return value

- Type: `float`
- The angle in radians, in -pi..pi.

## Remarks

The argument order is `atan2(y, x)`, like the C function, so it is the angle from the positive x axis to the point (x, y).

## Example

```tern
println(math.atan2(1.0, 0.0) == math.pi / 2);
```

## See also

- [math.atan](atan.md)

---

Part of the [`math`](index.md) namespace.
