# string.indexOf

The index of the first occurrence.

## Syntax

```tern
string.indexOf(substring)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `substring` | `string` | The text to find. |

## Return value

- Type: `int`
- The character index, or `-1`.

## Example

```tern
println("hello".indexOf("l"), "hello".indexOf("z"));
```

## See also

- [string.lastIndexOf](lastIndexOf.md)
- [string.contains](contains.md)

---

Part of the [`string` methods](index.md).
