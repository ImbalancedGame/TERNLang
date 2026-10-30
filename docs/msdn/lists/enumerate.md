# lists.enumerate

Index and value pairs.

## Syntax

```tern
lists.enumerate(list)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `list` | `list` | The list. |

## Return value

- Type: `list of list`
- `[index, value]` entries.

## Example

```tern
for (pair in lists.enumerate(["a", "b"])) { println(pair[0], pair[1]); }
```

## See also

- [list.each](../list/each.md)

---

Part of the [`lists`](index.md) namespace.
