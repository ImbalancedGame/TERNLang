# values

The values of a map, in insertion order.

## Syntax

```tern
values(map)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `map` | `map` | The map. |

## Return value

- Type: `list`
- The values.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The argument is not a map. |

## Example

```tern
println(values({"a": 1, "b": 2}));
```

## See also

- [keys](keys.md)
- [map.values](../map/values.md)

---

Part of the [`global`](index.md) namespace.
