# gl.program

Compiles and links a shader program.

## Syntax

```tern
gl.program(context, vertex, fragment)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `context` | `int` | A context handle. |
| `vertex` | `string` | GLSL for the vertex stage. |
| `fragment` | `string` | GLSL for the fragment stage. |

## Return value

- Type: `int`
- A program handle.

## Errors

| Error | Raised when |
| --- | --- |
| `GLError` | The driver's compile log, prefixed with `the vertex shader did not compile: `. |

## Example

```tern
let program = gl.program(surface, vertexSource, fragmentSource);
```

## See also

- [gl.draw](draw.md)

---

Part of the [`gl`](index.md) namespace.
