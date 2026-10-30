# list.join

Joins the items into a string.

## Syntax

```tern
list.join(separator?)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `separator` | `string` | Optional, `,` by default. |

## Return value

- Type: `string`
- The joined text; items are rendered with `str`.

## Example

```tern
println([1, 2, 3].join(" - "));
```

## See also

- [text.join](../text/join.md)
- [global.str](../global/str.md)

---

Part of the [`list` methods](index.md).
