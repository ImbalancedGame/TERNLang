# list.take

A copy of the first n items.

## Syntax

```tern
list.take(count)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `count` | `int` | How many; a count beyond the end takes everything. |

## Return value

- Type: `list`
- The copy.

## Example

```tern
println([1, 2, 3].take(2));
```

## See also

- [list.drop](drop.md)
- [list.slice](slice.md)

---

Part of the [`list` methods](index.md).
