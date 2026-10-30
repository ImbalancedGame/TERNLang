# d3d12.size

The size of a device's target.

## Syntax

```tern
d3d12.size(device)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `device` | `int` | A device handle. |

## Return value

- Type: `list of int`
- `[width, height]`.

## Example

```tern
println(d3d12.size(target));
```

## See also

- [d3d12.create](create.md)

---

Part of the [`d3d12`](index.md) namespace.
