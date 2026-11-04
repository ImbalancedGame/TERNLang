# d3d12.draw

Draws triangles, with the shader's constants.

## Syntax

```tern
d3d12.draw(device, pipeline, vertices, constants?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `device` | `int` | A device handle. |
| `pipeline` | `int` | A pipeline handle. |
| `vertices` | `list of number` | Six floats per vertex: `x, y, r, g, b, a`. |
| `constants` | `list of number` | Optional. Up to 64 numbers, in the shader's `register(b0)`, from the start. |

## Return value

`nil`.

## Remarks

Each call uploads into a fresh upload-heap buffer: correct and simple. A renderer that draws thousands of times a frame would rotate buffers instead.

The constants are what a *camera* is made of: they arrive as the shader's `cbuffer` in `register(b0)`, so a value that changes every frame - an eye position, a matrix, a clock - costs sixteen floats on the command list instead of a shader compile. The root signature declares all 64 dwords, and a draw sets as many as it is given from offset 0; the rest keep whatever the previous draw left there, so pass every value the shader reads.

Whole numbers are accepted and are converted to floats, because a shader cannot add an `int` to a `float4`.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The vertex count is not a multiple of six, or there are more than 64 constants. |
| `D3D12Error` | The vertex buffer cannot be created or the pipeline is not open. |

## Example

```tern
let camera = [-1.0, 0.0, 0.0, 0.73, 0.0, 1.0, 0.0, 0.55, 0.0, 0.0, -1.0, 0.0, 0.0, 1.15, 4.2, 0.0];
d3d12.draw(target, pipeline, fullscreen_triangle(), camera);
```

## See also

- [d3d12.pipeline](pipeline.md)
- [d3d12.pixels](pixels.md)

---

Part of the [`d3d12`](index.md) namespace.
