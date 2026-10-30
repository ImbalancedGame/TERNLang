# map.merge

A new map with another map's pairs merged in.

## Syntax

```tern
map.merge(other)
```

`receiver` is the map on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `other` | `map` | The map to merge; on a clash its value wins. |

## Return value

- Type: `map`
- The new map.

## Remarks

Shallow: a nested map is shared with the original, not copied.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The argument is not a map. |

## Example

```tern
println({"a": 1}.merge({"a": 2, "b": 3}));
```

## See also

- [map.set](set.md)
- [global.copy](../global/copy.md)

---

Part of the [`map` methods](index.md).
