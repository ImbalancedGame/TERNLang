# list.map

A new list of callback results.

## Syntax

```tern
list.map(callback)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `callback` | `function` | Called with each item; its result becomes the new item. |

## Return value

- Type: `list`
- The new list.

## Remarks

`map` can change the type: a list of strings can become a list of lengths. To discard the results, use `each`.

## Example

```tern
println([1, 2, 3].map(fn (x) { return x * x; }));
```

## See also

- [list.each](each.md)
- [list.filter](filter.md)
- [list.reduce](reduce.md)

---

Part of the [`list` methods](index.md).
