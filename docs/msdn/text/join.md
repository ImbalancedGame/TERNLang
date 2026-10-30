# text.join

Joins a list with a separator.

## Syntax

```tern
text.join(items, separator?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `items` | `list` | The items; each is rendered with `str`. |
| `separator` | `string` | Optional, `,` by default. |

## Return value

- Type: `string`
- The joined text.

## Example

```tern
println(text.join([1, 2, 3], " - "));
```

## See also

- [list.join](../list/join.md)

---

Part of the [`text`](index.md) namespace.
