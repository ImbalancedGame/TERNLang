# list.reduce

Folds the list into one value.

## Syntax

```tern
list.reduce(callback, initial?)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `callback` | `function` | Called as `callback(accumulator, item)`. |
| `initial` | `any` | Optional; without it the first item seeds the accumulator. |

## Return value

- Type: `any`
- The final accumulator; `nil` for an empty list with no initial value.

## Remarks

With an initial value the accumulator starts there and every item is visited; without one the first item is the accumulator and the rest are visited.

## Example

```tern
println([1, 2, 3].reduce(fn (a, b) { return a + b; }));
println([].reduce(fn (a, b) { return a + b; }, 0));
```

## See also

- [list.sum](sum.md)
- [list.map](map.md)

---

Part of the [`list` methods](index.md).
