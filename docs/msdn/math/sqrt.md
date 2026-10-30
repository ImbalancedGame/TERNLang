# math.sqrt

The square root.

## Syntax

```tern
math.sqrt(value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `number` | A non-negative number. |

## Return value

- Type: `float`
- The root; NaN for a negative argument rather than an error.

## Example

```tern
println(math.sqrt(9.0));
```

## See also

- [math.cbrt](cbrt.md)
- [math.pow](pow.md)

---

Part of the [`math`](index.md) namespace.
