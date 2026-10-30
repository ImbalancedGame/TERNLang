# random.choice

A random item from a list.

## Syntax

```tern
random.choice(list)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `list` | `list` | A non-empty list. |

## Return value

- Type: `any`
- The item; `nil` for an empty list.

## Example

```tern
println(random.choice(["a", "b", "c"]));
```

## See also

- [random.shuffle](shuffle.md)

---

Part of the [`random`](index.md) namespace.
