# copy

A shallow copy of a list or a map.

## Syntax

```tern
copy(value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `list\|map` | The container. |

## Return value

- Type: `list|map`
- A new container with the same items.

## Remarks

The items are shared, not cloned: changing a nested list through the copy changes it through the original too.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The value is neither a list nor a map. |

## Example

```tern
let a = [1, 2]; let b = copy(a); b.push(3); println(len(a), len(b));
```

## See also

- [list.clone](../list/clone.md)
- [map.clone](../map/clone.md)

---

Part of the [`global`](index.md) namespace.
