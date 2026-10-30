# list.min

The smallest item.

## Syntax

```tern
list.min()
```

`receiver` is the list on the left of the dot.

## Return value

- Type: `any`
- The item, or `nil` when the list is empty.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The items are not comparable. |

## Example

```tern
println([3, 1, 2].min());
```

## See also

- [list.max](max.md)
- [global.min](../global/min.md)

---

Part of the [`list` methods](index.md).
