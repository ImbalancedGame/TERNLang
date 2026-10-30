# math.pow

Raises a number to a power.

## Syntax

```tern
math.pow(base, exponent)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `base` | `number` | The base. |
| `exponent` | `number` | The exponent. |

## Return value

- Type: `float`
- The power.

## Remarks

Always a float; `**` on two integers gives an integer and raises on overflow.

## Example

```tern
println(math.pow(2.0, 10.0));
```

## See also

- [math.exp](exp.md)
- [math.sqrt](sqrt.md)

---

Part of the [`math`](index.md) namespace.
