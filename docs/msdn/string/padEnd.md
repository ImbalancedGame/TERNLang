# string.padEnd

Right-pads to a width.

## Syntax

```tern
string.padEnd(width, fill?)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `width` | `int` | The total width. |
| `fill` | `string` | Optional, a space by default. |

## Return value

- Type: `string`
- The padded string.

## Example

```tern
println("7".padEnd(3, ".") + "|");
```

## See also

- [string.padStart](padStart.md)

---

Part of the [`string` methods](index.md).
