# float.toInt

Truncates toward zero.

## Syntax

```tern
float.toInt()
```

`receiver` is the float on the left of the dot.

## Return value

- Type: `int`
- The integer part.

## Remarks

`2.9.toInt()` is `2` and `-2.9.toInt()` is `-2`; `math.floor` goes down instead and returns a float.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The value is NaN or too large for an integer. |

## Example

```tern
println(2.9.toInt(), (-2.9).toInt());
```

## See also

- [global.int](../global/int.md)
- [math.trunc](../math/trunc.md)

---

Part of the [`float` methods](index.md).
