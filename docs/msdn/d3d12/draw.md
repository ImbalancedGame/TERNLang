# d3d12.draw

Draws triangles.

## Syntax

```tern
d3d12.draw(device, pipeline, vertices)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `device` | `int` | A device handle. |
| `pipeline` | `int` | A pipeline handle. |
| `vertices` | `list of number` | Six floats per vertex: `x, y, r, g, b, a`. |

## Return value

`nil`.

## Remarks

Each call uploads into a fresh upload-heap buffer: correct and simple. A renderer that draws thousands of times a frame would rotate buffers instead.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The vertex count is not a multiple of six. |
| `D3D12Error` | The vertex buffer cannot be created or the pipeline is not open. |

## Example

```tern
d3d12.draw(target, pipeline, [-0.5, -0.5, 1, 0, 0, 1, 0.5, -0.5, 0, 1, 0, 1, 0, 0.5, 0, 0, 1, 1]);
```

## See also

- [d3d12.pipeline](pipeline.md)
- [d3d12.pixels](pixels.md)

---

Part of the [`d3d12`](index.md) namespace.
