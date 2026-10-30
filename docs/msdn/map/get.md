# map.get

The value for a key, or a default.

## Syntax

```tern
map.get(key, fallback?)
```

`receiver` is the map on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `key` | `any` | The key. |
| `fallback` | `any` | Optional, `nil` by default. |

## Return value

- Type: `any`
- The value, or the fallback.

## Remarks

Reading a missing key with `m[key]` raises; `get` is the way to ask without risking that. A key whose value is `nil` is present and returns `nil`, not the fallback.

## Example

```tern
let m = {"a": 1}; println(m.get("b", 0));
```

## See also

- [map.has](has.md)
- [map.set](set.md)

---

Part of the [`map` methods](index.md).
