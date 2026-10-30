# range.each

Calls a callback for each value.

## Syntax

```tern
range.each(callback)
```

`receiver` is the range on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `callback` | `function` | Called with each value. |

## Return value

`nil`.

## Remarks

No list is built, which is the point of iterating a range.

## Example

```tern
(0..<3).each(fn (i) { println(i); });
```

## See also

- [range.map](map.md)
- [list.each](../list/each.md)

---

Part of the [`range` methods](index.md).
