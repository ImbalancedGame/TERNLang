# map.has

True when the key exists.

## Syntax

```tern
map.has(key)
```

`receiver` is the map on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `key` | `any` | The key. |

## Return value

- Type: `bool`
- Whether it is present, even when its value is `nil`.

## Example

```tern
println({"a": 1}.has("a"));
```

## See also

- [map.get](get.md)
- [global.has](../global/has.md)

---

Part of the [`map` methods](index.md).
