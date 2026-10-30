# list.all

True when the callback accepts every item.

## Syntax

```tern
list.all(callback)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `callback` | `function` | The predicate. |

## Return value

- Type: `bool`
- Whether every item is accepted; true for an empty list.

## Example

```tern
println([2, 4].all(fn (x) { return x % 2 == 0; }));
```

## See also

- [list.any](any.md)

---

Part of the [`list` methods](index.md).
