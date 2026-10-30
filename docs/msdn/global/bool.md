# bool

Converts a value to a boolean.

## Syntax

```tern
bool(value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `any` | The value. |

## Return value

- Type: `bool`
- The truth value.

## Remarks

`nil`, `false`, `0`, `0.0`, `""`, `[]` and `{}` are false; everything else, including a range, is true.

## Example

```tern
println(bool(0), bool(""), bool([]), bool("0"));
```

## See also

- [type](type.md)

---

Part of the [`global`](index.md) namespace.
