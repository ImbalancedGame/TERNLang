# list.find

The first item the callback accepts.

## Syntax

```tern
list.find(callback)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `callback` | `function` | The predicate. |

## Return value

- Type: `any`
- The item, or `nil` when none is accepted.

## Example

```tern
println([1, 2, 3].find(fn (x) { return x > 1; }));
```

## See also

- [list.findIndex](findIndex.md)
- [list.filter](filter.md)

---

Part of the [`list` methods](index.md).
