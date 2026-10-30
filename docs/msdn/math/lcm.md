# math.lcm

The least common multiple.

## Syntax

```tern
math.lcm(a, b)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `a` | `int` | A positive integer. |
| `b` | `int` | A positive integer. |

## Return value

- Type: `int`
- The multiple.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | Either argument is zero or negative. |

## Example

```tern
println(math.lcm(4, 6));
```

## See also

- [math.gcd](gcd.md)

---

Part of the [`math`](index.md) namespace.
