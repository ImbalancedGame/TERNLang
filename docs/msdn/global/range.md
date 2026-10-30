# range

Creates an integer range.

## Syntax

```tern
range(stop, start?, stop?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `stop` | `int` | The exclusive end, when it is the only argument. |
| `start` | `int` | The inclusive start. |
| `stop` | `int` | The exclusive end. |

## Return value

- Type: `range`
- A range that can be iterated, indexed by `len`, or materialised with `toList`.

## Remarks

`range(5)` is `0..<5`, `range(2, 5)` is `2..<5`, and `range(0, 10, 2)` steps by two. A negative step counts down.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | An argument is not an integer. |

## Example

```tern
println(len(range(5)), range(2, 5).toList());
```

## See also

- [lists.range](../lists/range.md)
- [range.toList](../range/toList.md)

---

Part of the [`global`](index.md) namespace.
