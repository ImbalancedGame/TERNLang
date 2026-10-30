# list.sum

The sum of the numeric items.

## Syntax

```tern
list.sum()
```

`receiver` is the list on the left of the dot.

## Return value

- Type: `number`
- The total; integers stay integers, and a float makes it a float.

## Remarks

An empty list sums to `0`.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | An item is not a number. |

## Example

```tern
println([1, 2, 3].sum());
```

## See also

- [list.reduce](reduce.md)
- [list.min](min.md)

---

Part of the [`list` methods](index.md).
