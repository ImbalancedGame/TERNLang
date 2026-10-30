# list.sort

Sorts in place.

## Syntax

```tern
list.sort(comparator?)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `comparator` | `function` | Optional `fn (a, b)` returning a number; ascending when negative. |

## Return value

`nil`.

## Remarks

Stable: equal items keep their order, which is what makes sorting by one key after another work. `lists.sort` returns a copy instead.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The items are not comparable, or the comparator does not return a number. |

## Example

```tern
let xs = [3, 1, 2];
xs.sort();
println(xs);
```

## See also

- [lists.sort](../lists/sort.md)
- [list.reverse](reverse.md)

---

Part of the [`list` methods](index.md).
