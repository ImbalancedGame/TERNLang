# d3d12.clear

Clears the render target.

## Syntax

```tern
d3d12.clear(device, red, green, blue, alpha)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `device` | `int` | A device handle. |
| `red` | `number` | 0..1. |
| `green` | `number` | 0..1. |
| `blue` | `number` | 0..1. |
| `alpha` | `number` | 0..1. |

## Return value

`nil`.

## Remarks

Records a barrier to the render target state, a clear, and a barrier back to copy source; the call returns once the GPU has finished.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | A component is outside 0..1. |

## Example

```tern
d3d12.clear(target, 0.1, 0.1, 0.15, 1);
```

## See also

- [d3d12.draw](draw.md)
- [d3d12.pixels](pixels.md)

---

Part of the [`d3d12`](index.md) namespace.
