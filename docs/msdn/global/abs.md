# abs

The absolute value.

## Syntax

```tern
abs(value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `number` | The value. |

## Return value

- Type: `number`
- Its magnitude, of the same kind.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The argument is not a number. |

## Example

```tern
println(abs(-3), abs(-2.5));
```

## See also

- [math.abs](../math/abs.md)

---

Part of the [`global`](index.md) namespace.
