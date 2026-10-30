# string.reversed

The string backwards.

## Syntax

```tern
string.reversed()
```

`receiver` is the string on the left of the dot.

## Return value

- Type: `string`
- The reversed copy.

## Remarks

By character, so combining marks end up before the letter they modify; this is a text operation, not a rendering one.

## Example

```tern
println("abc".reversed());
```

## See also

- [list.reverse](../list/reverse.md)

---

Part of the [`string` methods](index.md).
