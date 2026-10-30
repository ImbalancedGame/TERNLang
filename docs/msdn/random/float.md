# random.float

A random float in a half-open range.

## Syntax

```tern
random.float(low?, high?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `low` | `number` | Optional, 0.0 by default. |
| `high` | `number` | Optional, 1.0 by default. |

## Return value

- Type: `float`
- In `[low, high)`.

## Example

```tern
println(random.float() < 1.0);
```

## See also

- [random.int](int.md)

---

Part of the [`random`](index.md) namespace.
