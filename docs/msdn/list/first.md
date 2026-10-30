# list.first

The first item.

## Syntax

```tern
list.first()
```

`receiver` is the list on the left of the dot.

## Return value

- Type: `any`
- The item, or `nil` when the list is empty.

## Remarks

`nil` is also a legal item, so test `isEmpty()` when that matters.

## Example

```tern
println([1, 2].first());
```

## See also

- [list.last](last.md)
- [list.take](take.md)

---

Part of the [`list` methods](index.md).
