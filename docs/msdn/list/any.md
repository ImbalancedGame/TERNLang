# list.any

True when the callback accepts at least one item.

## Syntax

```tern
list.any(callback)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `callback` | `function` | The predicate. |

## Return value

- Type: `bool`
- Whether any item is accepted.

## Remarks

Stops at the first accepted item.

## Example

```tern
println([1, 2].any(fn (x) { return x == 2; }));
```

## See also

- [list.all](all.md)
- [list.find](find.md)

---

Part of the [`list` methods](index.md).
