# text.pad

Pads text to a width on the right.

## Syntax

```tern
text.pad(text, width, fill?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `text` | `string` | The text. |
| `width` | `int` | The target width. |
| `fill` | `string` | Optional, a space by default. |

## Return value

- Type: `string`
- The padded text; unchanged when already wide enough.

## Example

```tern
println(text.pad("ab", 5, ".") + "|");
```

## See also

- [string.padStart](../string/padStart.md)
- [string.padEnd](../string/padEnd.md)

---

Part of the [`text`](index.md) namespace.
