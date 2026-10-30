# math.trunc

Drops the fractional part.

## Syntax

```tern
math.trunc(value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `number` | The value. |

## Return value

- Type: `float`
- The value toward zero, as a float: `-1.0`.

## Remarks

`trunc(-1.5)` is `-1.0` where `floor(-1.5)` is `-2.0`.

## Example

```tern
println(math.trunc(-1.5));
```

## See also

- [math.floor](floor.md)

---

Part of the [`math`](index.md) namespace.
