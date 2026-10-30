# keys

The keys of a map, in insertion order.

## Syntax

```tern
keys(map)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `map` | `map` | The map. |

## Return value

- Type: `list`
- The keys.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The argument is not a map. |

## Example

```tern
println(keys({"a": 1, "b": 2}));
```

## See also

- [values](values.md)
- [map.keys](../map/keys.md)

---

Part of the [`global`](index.md) namespace.
