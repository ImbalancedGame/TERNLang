# gl

OpenGL, loaded at run time so nothing is linked.

libEGL is opened with `dlopen`/`NativeLibrary` on first use, a surfaceless
pbuffer supplies a context with no window server, and the GL entry points come
from `eglGetProcAddress`. A script that never renders needs no driver, and a
machine without one gets a catchable `GLError`.

The context is OpenGL 3.3 core. Vertices carry `x, y, r, g, b, a`: attribute 0
is the `vec2` position and attribute 1 the `vec4` colour.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`available`](available.md) | 0..0 | True when libEGL could be loaded. |
| [`clear`](clear.md) | 5..5 | Fills the surface with a colour. |
| [`close`](close.md) | 1..1 | Destroys a context and its programs. |
| [`create`](create.md) | 2..2 | Creates an offscreen context and returns a handle. |
| [`draw`](draw.md) | 3..3 | Draws triangles. |
| [`info`](info.md) | 0..0 | Describes the driver. |
| [`pixels`](pixels.md) | 1..1 | Reads the surface back as RGBA bytes. |
| [`program`](program.md) | 3..3 | Compiles and links a shader program. |
| [`save`](save.md) | 2..2 | Saves the surface as a PNG. |
| [`size`](size.md) | 1..1 | The size of a context. |

## Example

```tern
import "std:gl";

let surface = gl.create(640, 480);
gl.clear(surface, 0.1, 0.1, 0.15, 1);
let program = gl.program(surface, vertexSource, fragmentSource);
gl.draw(surface, program, [-0.5, -0.5, 1, 0, 0, 1, 0.5, -0.5, 0, 1, 0, 1, 0, 0.5, 0, 0, 1, 1]);
gl.save(surface, "triangle.png");
gl.close(surface);
```

