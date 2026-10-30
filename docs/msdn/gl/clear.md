# gl.clear

Fills the surface with a colour.

## Syntax

```tern
gl.clear(context, red, green, blue, alpha)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `context` | `int` | A context handle. |
| `red` | `number` | 0..1. |
| `green` | `number` | 0..1. |
| `blue` | `number` | 0..1. |
| `alpha` | `number` | 0..1. |

## Return value

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | A component is outside 0..1. |

## Example

```tern
gl.clear(surface, 0.1, 0.1, 0.15, 1);
```

## See also

- [gl.draw](draw.md)
- [gl.pixels](pixels.md)

---

Part of the [`gl`](index.md) namespace.
