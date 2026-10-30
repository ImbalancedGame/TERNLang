# list.remove

Removes and returns the item at an index.

## Syntax

```tern
list.remove(index)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `index` | `int` | The position; negative counts from the end. |

## Return value

- Type: `any`
- The removed item, or `nil` when the index is out of range.

## Example

```tern
let xs = [1, 2, 3];
println(xs.remove(1), xs);
```

## See also

- [list.removeValue](removeValue.md)
- [list.pop](pop.md)

---

Part of the [`list` methods](index.md).
