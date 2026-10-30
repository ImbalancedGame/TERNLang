# text.split

Splits text on a separator.

## Syntax

```tern
text.split(text, separator)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `text` | `string` | The text. |
| `separator` | `string` | The separator. |

## Return value

- Type: `list of string`
- The pieces.

## Example

```tern
println(text.split("a,b", ","));
```

## See also

- [string.split](../string/split.md)

---

Part of the [`text`](index.md) namespace.
