# gl.save

Saves the surface as a PNG.

## Syntax

```tern
gl.save(context, path)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `context` | `int` | A context handle. |
| `path` | `string` | Where to write it. |

## Return value

`nil`.

## Remarks

The PNG is written with stored deflate blocks: valid, larger on disk, and no zlib in sight.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The file cannot be written. |

## Example

```tern
gl.save(surface, "frame.png");
```

## See also

- [gl.pixels](pixels.md)

---

Part of the [`gl`](index.md) namespace.
