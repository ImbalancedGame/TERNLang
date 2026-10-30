# map.remove

Deletes a key.

## Syntax

```tern
map.remove(key)
```

`receiver` is the map on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `key` | `any` | The key. |

## Return value

- Type: `any`
- The removed value, or `nil` when the key was absent.

## Example

```tern
let m = {"a": 1}; println(m.remove("a"), m.isEmpty());
```

## See also

- [map.set](set.md)

---

Part of the [`map` methods](index.md).
