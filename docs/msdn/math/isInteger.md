# math.isInteger

True for whole numbers.

## Syntax

```tern
math.isInteger(value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `any` | The value. |

## Return value

- Type: `bool`
- Whether it is an integer or a whole float.

## Remarks

`math.isInteger(2.0)` is true; `math.isInteger(2.5)` is false and so is `math.isInteger("2")`.

## Example

```tern
println(math.isInteger(2.0), math.isInteger(2.5));
```

## See also

- [math.isNaN](isNaN.md)

---

Part of the [`math`](index.md) namespace.
