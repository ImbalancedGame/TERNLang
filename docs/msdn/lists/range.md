# lists.range

A list of integers from start to end.

## Syntax

```tern
lists.range(start, stop?, step?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `start` | `int` | The first value, or the stop when it is the only argument. |
| `stop` | `int` | The exclusive end. |
| `step` | `int` | Optional, 1 by default; may be negative. |

## Return value

- Type: `list`
- The integers.

## Remarks

The list is materialised, unlike `range()`, which stays symbolic. Use this when you need to index or mutate the result.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | `step` is zero. |

## Example

```tern
println(lists.range(5), lists.range(2, 8, 2));
```

## See also

- [global.range](../global/range.md)
- [range.toList](../range/toList.md)

---

Part of the [`lists`](index.md) namespace.
