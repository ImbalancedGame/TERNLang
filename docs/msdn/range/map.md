# range.map

A list of callback results over the range.

## Syntax

```tern
range.map(callback)
```

`receiver` is the range on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `callback` | `function` | Called with each value. |

## Return value

- Type: `list`
- The results.

## Example

```tern
println((0..<3).map(fn (i) { return i * 2; }));
```

## See also

- [range.each](each.md)
- [list.map](../list/map.md)

---

Part of the [`range` methods](index.md).
