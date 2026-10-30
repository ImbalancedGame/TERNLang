# math.floor

Rounds down to an integer.

## Syntax

```tern
math.floor(value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `number` | The value. |

## Return value

- Type: `float`
- The largest whole number not greater than the value, as a float: `-2.0`.

## Example

```tern
println(math.floor(-1.5), math.floor(1.5));
```

## See also

- [math.ceil](ceil.md)
- [math.trunc](trunc.md)

---

Part of the [`math`](index.md) namespace.
