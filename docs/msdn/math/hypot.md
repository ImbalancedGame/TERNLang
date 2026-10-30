# math.hypot

The length of a vector.

## Syntax

```tern
math.hypot(x, y)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `x` | `number` | The horizontal component. |
| `y` | `number` | The vertical component. |

## Return value

- Type: `float`
- sqrt(x*x + y*y), computed without intermediate overflow.

## Example

```tern
println(math.hypot(3.0, 4.0));
```

## See also

- [math.sqrt](sqrt.md)
- [math.atan2](atan2.md)

---

Part of the [`math`](index.md) namespace.
