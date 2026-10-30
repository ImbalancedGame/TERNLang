# lists.repeat

A list with one value repeated.

## Syntax

```tern
lists.repeat(value, count)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `any` | The value. |
| `count` | `int` | How many copies, at least 0. |

## Return value

- Type: `list`
- The new list.

## Remarks

The value is copied, so a repeated list is N independent lists.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | `count` is negative. |

## Example

```tern
println(lists.repeat(0, 4));
```

## See also

- [list.concat](../list/concat.md)

---

Part of the [`lists`](index.md) namespace.
