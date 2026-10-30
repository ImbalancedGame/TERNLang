# string.chars

The string as single-character strings.

## Syntax

```tern
string.chars()
```

`receiver` is the string on the left of the dot.

## Return value

- Type: `list of string`
- One item per character, not per byte or UTF-16 unit.

## Example

```tern
println("héllo".chars());
```

## See also

- [len](len.md)
- [string.count](count.md)

---

Part of the [`string` methods](index.md).
