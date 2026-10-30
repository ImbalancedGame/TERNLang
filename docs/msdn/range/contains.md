# range.contains

True when a number is inside the range.

## Syntax

```tern
range.contains(value)
```

`receiver` is the range on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `number` | The number. |

## Return value

- Type: `bool`
- Whether it is in range.

## Remarks

The end is exclusive: `(0..<5).contains(5)` is false.

## Example

```tern
println((0..<5).contains(4));
```

## See also

- [range.each](each.md)

---

Part of the [`range` methods](index.md).
