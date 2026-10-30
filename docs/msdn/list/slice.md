# list.slice

A copy of a half-open index range.

## Syntax

```tern
list.slice(start?, end?)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `start` | `int` | Optional, 0 by default; negative counts from the end. |
| `end` | `int` | Optional, the length by default; negative counts from the end. |

## Return value

- Type: `list`
- The copy.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | A bound is out of range after adjustment. |

## Example

```tern
println([1, 2, 3, 4].slice(1, 3));
```

## See also

- [list.take](take.md)
- [list.drop](drop.md)

---

Part of the [`list` methods](index.md).
