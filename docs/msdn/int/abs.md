# int.abs

The absolute value.

## Syntax

```tern
int.abs()
```

`receiver` is the int on the left of the dot.

## Return value

- Type: `int`
- The magnitude.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The value is the smallest integer, whose magnitude does not fit. |

## Example

```tern
println((-3).abs());
```

## See also

- [math.abs](../math/abs.md)
- [global.abs](../global/abs.md)

---

Part of the [`int` methods](index.md).
