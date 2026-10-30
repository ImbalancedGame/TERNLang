# list.filter

A new list of the items the callback accepts.

## Syntax

```tern
list.filter(callback)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `callback` | `function` | Called with each item; a truthy result keeps it. |

## Return value

- Type: `list`
- The new list.

## Remarks

Truthiness is `bool()`: `0` and `""` are false, so a predicate that returns `0` for success keeps nothing.

## Example

```tern
println([1, 2, 3, 4].filter(fn (x) { return x % 2 == 0; }));
```

## See also

- [list.map](map.md)
- [list.find](find.md)
- [list.any](any.md)

---

Part of the [`list` methods](index.md).
