# d3d12

Direct3D 12: device, render target, pipeline and draw.

`d3d12.dll` is opened at run time and every call goes through the COM vtable at
the slot the Windows SDK gives it. The structures are mirrored by hand, with the
sizes and offsets asserted against the real `d3d12.h` by the Windows build. HLSL
is compiled at run time by `d3dcompiler_47.dll`.

This is an offscreen renderer: surfaces are pbuffer-like targets that can be read
back as pixels and saved as PNG. There is no swap chain and no window.

The pipeline takes HLSL with the entry point `main` in both stages; the vertex
input is `POSITION` (`float3`) followed by `COLOR` (`float4`).

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`available`](available.md) | 0..0 | True when the Direct3D 12 library could be loaded. |
| [`clear`](clear.md) | 5..5 | Clears the render target. |
| [`close`](close.md) | 1..1 | Releases the device and everything it owns. |
| [`closeWindow`](closeWindow.md) | 1..1 | Closes a window. |
| [`create`](create.md) | 2..2 | Creates a device and an offscreen render target. |
| [`draw`](draw.md) | 3..3 | Draws triangles. |
| [`info`](info.md) | 0..0 | Describes the adapter and the binding. |
| [`pipeline`](pipeline.md) | 3..3 | Compiles HLSL and builds a pipeline state. |
| [`pixels`](pixels.md) | 1..1 | Reads the target back as RGBA bytes. |
| [`present`](present.md) | 2..2 | Shows the target in the window. |
| [`save`](save.md) | 2..2 | Saves the target as a PNG. |
| [`size`](size.md) | 1..1 | The size of a device's target. |
| [`window`](window.md) | 3..3 | Opens a window to show frames in. |

## Example

```tern
import "std:d3d12";

let target = d3d12.create(640, 480);
d3d12.clear(target, 0.1, 0.1, 0.15, 1);
let pipeline = d3d12.pipeline(target, vertexSource, pixelSource);
d3d12.draw(target, pipeline, [-0.5, -0.5, 1, 0, 0, 1, 0.5, -0.5, 0, 1, 0, 1, 0, 0.5, 0, 0, 1, 1]);
d3d12.save(target, "triangle.png");
d3d12.close(target);
```

