# math.sign

The sign of a number.

## Syntax

```tern
math.sign(value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `number` | The value. |

## Return value

- Type: `int`
- `-1`, `0` or `1`, even for a float argument.

## Example

```tern
println(math.sign(-3), math.sign(0), math.sign(0.5));
```

## See also

- [math.abs](abs.md)

---

Part of the [`math`](index.md) namespace.
