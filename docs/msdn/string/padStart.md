# string.padStart

Left-pads to a width.

## Syntax

```tern
string.padStart(width, fill?)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `width` | `int` | The total width. |
| `fill` | `string` | Optional, a space by default; taken cyclically. |

## Return value

- Type: `string`
- The padded string, or the original when it is already wide enough.

## Example

```tern
println("7".padStart(3, "0"));
```

## See also

- [string.padEnd](padEnd.md)
- [text.pad](../text/pad.md)

---

Part of the [`string` methods](index.md).
