# int.toFloat

The same value as a float.

## Syntax

```tern
int.toFloat()
```

`receiver` is the int on the left of the dot.

## Return value

- Type: `float`
- The converted value; precision is lost above 2^53.

## Example

```tern
println(2.toFloat() / 4.0);
```

## See also

- [global.float](../global/float.md)

---

Part of the [`int` methods](index.md).
