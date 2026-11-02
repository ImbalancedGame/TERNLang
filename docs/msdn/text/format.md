# text.format

Fills placeholders with arguments, each taking a format specification.

## Syntax

```tern
text.format(template, values...)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `template` | `string` | Text with `{}`, `{0}` or `{:spec}` placeholders. |
| `values` | `any` | The values to substitute. |

## Return value

- Type: `string`
- The filled text.

## Remarks

`{}` consumes the next argument and `{0}`, `{1}` index explicitly and may repeat; either
may carry a specification after a colon. The grammar is the part of Python's that
programs actually use:

```
{[[fill]align][sign][0][width][,][.precision][type]}
```

| Part | Meaning |
| --- | --- |
| `align` | `<` left, `>` right, `^` centre. Numbers default to right, text to left. |
| `fill` | The character to pad with, written before an align: `{:*^9}`. |
| `sign` | `+` always shows a sign, a space reserves one for a positive number. |
| `0` | Pads with zeros to the width, after any sign: `{:08.3f}` is `-003.142`. |
| `width` | Minimum width, up to 4096. |
| `,` | Groups thousands: `{:,}` is `1,234,567`. |
| `.precision` | Digits after the point for `f`, `e` and `%`; a maximum length for text. |
| `type` | `d` whole, `f` fixed, `e` scientific, `x` `X` `o` `b` bases, `%` percent, `s` text. |

A placeholder with no type prints exactly what `str` would, so adding a width never
changes the digits: `format("{:>8}", 3.0)` is `"     3.0"`. The `%` type shows one
decimal place unless a precision is given. `d`, `x`, `o` and `b` refuse a value with
a fractional part rather than truncating it, and say to use `{:.0f}` instead.

Everything is rendered with the invariant culture, so a program's output does not
depend on the machine's locale.

## Errors

| Error | Raised when |
| --- | --- |
| `FormatError` | A placeholder has no matching argument, or its specification is not one. The message names the offending specification and the types it accepts. |
| `TypeError` | A numeric type such as `d` or `f` was given something that is not a number. |

## Example

```tern
println(text.format("{:>8.2f}|", 3.14159));
println(text.format("{:,} of {:.1%}", 1234567, 0.1234));
println(text.format("{0} then {1} then {0}", "a", "b"));
```

## See also

- [string.format](../string/format.md)
- [global.str](../global/str.md)
- [global.repr](../global/repr.md)

---

Part of the [`text`](index.md) namespace.
