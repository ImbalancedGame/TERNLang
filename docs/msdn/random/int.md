# random.int

A random integer in an inclusive range.

## Syntax

```tern
random.int(low, high?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `low` | `int` | The lower bound, or the upper bound when it is the only argument. |
| `high` | `int` | The upper bound, inclusive. |

## Return value

- Type: `int`
- The value; bounds given in the wrong order are swapped.

## Example

```tern
println(random.int(1, 6));
```

## See also

- [random.float](float.md)
- [random.choice](choice.md)

---

Part of the [`random`](index.md) namespace.
