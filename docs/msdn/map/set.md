# map.set

Inserts or replaces a key.

## Syntax

```tern
map.set(key, value)
```

`receiver` is the map on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `key` | `any` | The key, compared by value. |
| `value` | `any` | The value. |

## Return value

- Type: `map`
- The receiver, so calls can chain.

## Remarks

A new key is added at the end, which is the order `keys()` reports; replacing an existing key keeps its position.

## Example

```tern
let m = {}; m.set("a", 1).set("b", 2); println(m);
```

## See also

- [map.get](get.md)
- [map.remove](remove.md)

---

Part of the [`map` methods](index.md).
