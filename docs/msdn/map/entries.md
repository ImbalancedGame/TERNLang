# map.entries

Key and value pairs.

## Syntax

```tern
map.entries()
```

`receiver` is the map on the left of the dot.

## Return value

- Type: `list of list`
- `[key, value]` entries, in insertion order.

## Example

```tern
for (entry in {"a": 1}.entries()) { println(entry[0], entry[1]); }
```

## See also

- [map.keys](keys.md)

---

Part of the [`map` methods](index.md).
