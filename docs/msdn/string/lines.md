# string.lines

The non-empty lines of a text.

## Syntax

```tern
string.lines()
```

`receiver` is the string on the left of the dot.

## Return value

- Type: `list of string`
- Lines without their terminators.

## Remarks

Both `\n` and `\r\n` end a line, and a trailing newline does not create an empty last line.

## Example

```tern
println("a\r\nb\n".lines());
```

## See also

- [string.split](split.md)

---

Part of the [`string` methods](index.md).
