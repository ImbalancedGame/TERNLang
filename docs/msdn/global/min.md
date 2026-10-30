# min

The smallest of its arguments.

## Syntax

```tern
min(values...)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `values` | `number\|string` | One or more comparable values. |

## Return value

- Type: `any`
- The smallest.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | Called with no arguments. |
| `TypeError` | The values are not comparable. |

## Example

```tern
println(min(3, 1, 2), min([3, 1, 2]));
```

## See also

- [max](max.md)
- [list.min](../list/min.md)

---

Part of the [`global`](index.md) namespace.
