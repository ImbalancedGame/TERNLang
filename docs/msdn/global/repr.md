# repr

Converts a value to a debug representation.

## Syntax

```tern
repr(value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `any` | The value. |

## Return value

- Type: `string`
- A form that quotes strings and shows types where they differ.

## Remarks

`str` is for the user, `repr` is for the programmer: `str("a")` is `a`, `repr("a")` is `"a"`.

## Example

```tern
println(repr("a"), str("a"));
```

## See also

- [str](str.md)

---

Part of the [`global`](index.md) namespace.
