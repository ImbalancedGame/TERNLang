# gl.draw

Draws triangles.

## Syntax

```tern
gl.draw(context, program, vertices)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `context` | `int` | A context handle. |
| `program` | `int` | A program handle. |
| `vertices` | `list of number` | Six floats per vertex: `x, y, r, g, b, a`, three per triangle. |

## Return value

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The vertex count is not a multiple of six. |
| `TypeError` | An element is not a number. |

## Example

```tern
gl.draw(surface, program, [-0.5, -0.5, 1, 0, 0, 1, 0.5, -0.5, 0, 1, 0, 1, 0, 0.5, 0, 0, 1, 1]);
```

## See also

- [gl.program](program.md)
- [gl.pixels](pixels.md)

---

Part of the [`gl`](index.md) namespace.
