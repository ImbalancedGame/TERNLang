# math.nan

A quiet NaN.

## Syntax

```tern
math.nan()
```

## Parameters

_None._

## Return value

- Type: `float`
- A value that is not equal to anything, including itself.

## Remarks

Compare with `math.isNaN`, never with `==`.

## Example

```tern
println(math.nan == math.nan, math.isNaN(math.nan));
```

## See also

- [math.isNaN](isNaN.md)

---

Part of the [`math`](index.md) namespace.
