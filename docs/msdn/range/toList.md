# range.toList

Materialises the range as a list.

## Syntax

```tern
range.toList()
```

`receiver` is the range on the left of the dot.

## Return value

- Type: `list of int`
- Every value the range yields.

## Remarks

A range of a hundred million is cheap; its list is not.

## Example

```tern
println((0..<3).toList());
```

## See also

- [global.range](../global/range.md)
- [lists.range](../lists/range.md)

---

Part of the [`range` methods](index.md).
