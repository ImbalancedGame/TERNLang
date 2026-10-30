# gl.pixels

Reads the surface back as RGBA bytes.

## Syntax

```tern
gl.pixels(context)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `context` | `int` | A context handle. |

## Return value

- Type: `list of int`
- `width * height * 4` bytes, bottom row first, like `glReadPixels`.

## Example

```tern
println(gl.pixels(surface)[0..4]);
```

## See also

- [gl.save](save.md)

---

Part of the [`gl`](index.md) namespace.
