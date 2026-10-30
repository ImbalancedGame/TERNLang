# lists.chunk

Splits a list into lists of at most n items.

## Syntax

```tern
lists.chunk(list, size)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `list` | `list` | The list. |
| `size` | `int` | The chunk size, at least 1. |

## Return value

- Type: `list of list`
- The chunks; the last may be shorter.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | `size` is less than 1. |

## Example

```tern
println(lists.chunk([1, 2, 3, 4, 5], 2));
```

## See also

- [list.take](../list/take.md)
- [list.slice](../list/slice.md)

---

Part of the [`lists`](index.md) namespace.
