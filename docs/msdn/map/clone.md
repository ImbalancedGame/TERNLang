# map.clone

A shallow copy.

## Syntax

```tern
map.clone()
```

`receiver` is the map on the left of the dot.

## Return value

- Type: `map`
- A new map with the same pairs, in the same order.

## Example

```tern
let a = {"x": 1}; let b = a.clone(); b.set("y", 2); println(len(a), len(b));
```

## See also

- [global.copy](../global/copy.md)
- [map.merge](merge.md)

---

Part of the [`map` methods](index.md).
