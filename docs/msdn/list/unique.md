# list.unique

A new list without duplicates.

## Syntax

```tern
list.unique()
```

`receiver` is the list on the left of the dot.

## Return value

- Type: `list`
- The new list, in first-seen order.

## Remarks

Compares with `==`, so `1` and `1.0` are the same item and two equal maps are one entry.

## Example

```tern
println([1, 1, 2].unique());
```

## See also

- [list.contains](contains.md)

---

Part of the [`list` methods](index.md).
