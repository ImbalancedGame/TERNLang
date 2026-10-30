# string.trim

The string without leading and trailing whitespace.

## Syntax

```tern
string.trim()
```

`receiver` is the string on the left of the dot.

## Return value

- Type: `string`
- The trimmed copy.

## Remarks

Whitespace is whatever Unicode calls it, so a non-breaking space is kept and a tab is not.

## Example

```tern
println("  a  ".trim() + "|");
```

## See also

- [string.trimStart](trimStart.md)
- [string.trimEnd](trimEnd.md)

---

Part of the [`string` methods](index.md).
