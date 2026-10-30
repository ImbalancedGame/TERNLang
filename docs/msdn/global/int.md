# int

Converts a value to an integer.

## Syntax

```tern
int(value, radix?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `any` | The value. |
| `radix` | `int` | Optional, 2..36; only used when the value is a string. |

## Return value

- Type: `int`
- The integer.

## Remarks

A float truncates toward zero; `true` is 1 and `nil` is 0. A string is trimmed first, and with no radix it may be integral text (`"42"`) or a float (`"4.9"`, which truncates).

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | A string cannot be parsed. |
| `TypeError` | The value cannot be converted. |

## Example

```tern
println(int("ff", 16), int(4.9), int(true), int(-4.9));
```

## See also

- [float](float.md)
- [string.toInt](../string/toInt.md)

---

Part of the [`global`](index.md) namespace.
