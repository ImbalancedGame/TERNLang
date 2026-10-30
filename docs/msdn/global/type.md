# type

The type name of a value.

## Syntax

```tern
type(value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `any` | Any value. |

## Return value

- Type: `string`
- `nil`, `bool`, `int`, `float`, `string`, `list`, `map`, `range`, `function`, `native` or a struct's name.

## Example

```tern
println(type(nil), type(1), type(1.0), type("s"), type([]), type({}));
```

## See also

- [bool](bool.md)
- [str](str.md)

---

Part of the [`global`](index.md) namespace.
