# d3d12.pipeline

Compiles HLSL and builds a pipeline state.

## Syntax

```tern
d3d12.pipeline(device, vertex, pixel)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `device` | `int` | A device handle. |
| `vertex` | `string` | HLSL for the vertex stage; the entry point is `main`. |
| `pixel` | `string` | HLSL for the pixel stage. |

## Return value

- Type: `int`
- A pipeline handle.

## Remarks

Also creates the (empty) root signature. The input layout is fixed: `POSITION` as `float3` at offset 0 and `COLOR` as `float4` at offset 12.

## Errors

| Error | Raised when |
| --- | --- |
| `D3D12Error` | The compiler is missing, or names the first error in the shader. |

## Example

```tern
let pipeline = d3d12.pipeline(target, vertexSource, pixelSource);
```

## See also

- [d3d12.draw](draw.md)

---

Part of the [`d3d12`](index.md) namespace.
