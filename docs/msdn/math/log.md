# math.log

The logarithm, in any base.

## Syntax

```tern
math.log(value, base?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `number` | A positive number. |
| `base` | `number` | Optional; e by default. |

## Return value

- Type: `float`
- The logarithm; NaN for a non-positive value.

## Example

```tern
println(math.log(math.e), math.log(8.0, 2.0));
```

## See also

- [math.log2](log2.md)
- [math.log10](log10.md)

---

Part of the [`math`](index.md) namespace.
