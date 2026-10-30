# map.contains

True when the key exists; an alias of `has`.

## Syntax

```tern
map.contains(key)
```

`receiver` is the map on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `key` | `any` | The key. |

## Return value

- Type: `bool`
- Whether it is present.

## Example

```tern
println({"a": 1}.contains("a"));
```

## See also

- [map.has](has.md)

---

Part of the [`map` methods](index.md).
