# string.words

Splits on runs of whitespace.

## Syntax

```tern
string.words()
```

`receiver` is the string on the left of the dot.

## Return value

- Type: `list of string`
- The words; empty pieces are dropped.

## Example

```tern
println(" a  b ".words());
```

## See also

- [string.split](split.md)

---

Part of the [`string` methods](index.md).
