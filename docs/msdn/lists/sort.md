# lists.sort

A sorted copy, leaving the original alone.

## Syntax

```tern
lists.sort(list, comparator?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `list` | `list` | The list. |
| `comparator` | `function` | Optional `fn (a, b)` returning a number; ascending when negative. |

## Return value

- Type: `list`
- The sorted copy.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The items are not comparable, or the comparator does not return a number. |

## Example

```tern
let xs = [3, 1, 2]; println(lists.sort(xs), xs);
```

## See also

- [list.sort](../list/sort.md)

---

Part of the [`lists`](index.md) namespace.
