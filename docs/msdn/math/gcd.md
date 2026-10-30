# math.gcd

The greatest common divisor.

## Syntax

```tern
math.gcd(a, b)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `a` | `int` | A positive integer. |
| `b` | `int` | A positive integer. |

## Return value

- Type: `int`
- The divisor.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | Either argument is zero or negative. |

## Example

```tern
println(math.gcd(12, 18));
```

## See also

- [math.lcm](lcm.md)

---

Part of the [`math`](index.md) namespace.
