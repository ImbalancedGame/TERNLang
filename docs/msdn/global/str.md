# str

Converts a value to its string form.

## Syntax

```tern
str(value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `any` | The value. |

## Return value

- Type: `string`
- Its display form.

## Remarks

Strings are returned unchanged; inside a list or a map they are quoted, which is what `repr` does everywhere. A float always shows its fractional part: `str(2.0)` is `2.0`, not `2`.

## Example

```tern
println(str(1.5) + "!");
println(str([1, 2]));
```

## See also

- [repr](repr.md)
- [global.format](format.md)

---

Part of the [`global`](index.md) namespace.
