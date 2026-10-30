# max

The largest of its arguments.

## Syntax

```tern
max(values...)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `values` | `number\|string` | One or more comparable values. |

## Return value

- Type: `any`
- The largest.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | Called with no arguments. |

## Example

```tern
println(max(3, 1, 2));
```

## See also

- [min](min.md)
- [list.max](../list/max.md)

---

Part of the [`global`](index.md) namespace.
