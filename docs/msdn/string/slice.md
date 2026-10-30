# string.slice

A substring from start to end.

## Syntax

```tern
string.slice(start?, end?)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `start` | `int` | Optional, 0 by default; negative counts from the end. |
| `end` | `int` | Optional, the length by default; negative counts from the end. |

## Return value

- Type: `string`
- The substring.

## Remarks

The end is exclusive, like everywhere else in the language. Bounds are indices into the UTF-16 text, like `len()`; a bound that lands inside a character above U+FFFF resolves to the start of that character, so a surrogate pair is never cut in half.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | A bound is outside the string after adjustment. |

## Example

```tern
println("hello".slice(1, 3), "hello".slice(-2));
```

## See also

- [string.substring](substring.md)
- [list.slice](../list/slice.md)

---

Part of the [`string` methods](index.md).
