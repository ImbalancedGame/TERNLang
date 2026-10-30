# random.seed

Seeds the generator.

## Syntax

```tern
random.seed(seed)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `seed` | `int` | The seed. |

## Return value

`nil`.

## Remarks

A seeded run is reproducible byte for byte, including across the two back ends only when the generator is the same — it is not, so treat the sequence as reproducible per build, not across them.

## Example

```tern
random.seed(7);
println(random.int(0, 99));
```

## See also

- [random.int](int.md)

---

Part of the [`random`](index.md) namespace.
