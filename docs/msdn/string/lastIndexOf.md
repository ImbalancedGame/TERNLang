# string.lastIndexOf

The index of the last occurrence.

## Syntax

```tern
string.lastIndexOf(substring)
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
println("hello".lastIndexOf("l"));
```

## See also

- [string.indexOf](indexOf.md)

---

Part of the [`string` methods](index.md).
