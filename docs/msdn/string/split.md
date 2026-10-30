# string.split

Splits the string on a separator.

## Syntax

```tern
string.split(separator?)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `separator` | `string` | Optional, a space by default; `\"\"` splits into characters. |

## Return value

- Type: `list of string`
- The pieces, in order.

## Remarks

Adjacent separators produce empty pieces, so `"a,,b".split(",")` has three items. Use `words()` to drop empty pieces instead.

## Example

```tern
println("a,b,,c".split(","));
println("abc".split(""));
```

## See also

- [string.words](words.md)
- [string.lines](lines.md)
- [text.split](../text/split.md)

---

Part of the [`string` methods](index.md).
