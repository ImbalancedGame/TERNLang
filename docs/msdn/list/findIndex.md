# list.findIndex

The index of the first item the callback accepts.

## Syntax

```tern
list.findIndex(callback)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `callback` | `function` | The predicate. |

## Return value

- Type: `int`
- The index, or `-1`.

## Example

```tern
println([1, 2, 3].findIndex(fn (x) { return x > 1; }));
```

## See also

- [list.find](find.md)

---

Part of the [`list` methods](index.md).
