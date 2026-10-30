# list.concat

A new list with another appended.

## Syntax

```tern
list.concat(other)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `other` | `list` | The list to append. |

## Return value

- Type: `list`
- The new list; both inputs are unchanged.

## Example

```tern
println([1, 2].concat([3]));
```

## See also

- [list.push](push.md)
- [list.flatten](flatten.md)

---

Part of the [`list` methods](index.md).
