# has

True when a map has a key.

## Syntax

```tern
has(map, key)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `map` | `map` | The map. |
| `key` | `any` | The key, compared by value. |

## Return value

- Type: `bool`
- Whether the key is present.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The first argument is not a map. |

## Example

```tern
println(has({"a": 1}, "a"), has({"a": 1}, "b"));
```

## See also

- [keys](keys.md)
- [map.has](../map/has.md)

---

Part of the [`global`](index.md) namespace.
