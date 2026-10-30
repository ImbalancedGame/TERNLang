# gl.create

Creates an offscreen context and returns a handle.

## Syntax

```tern
gl.create(width, height)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `width` | `int` | 1..8192. |
| `height` | `int` | 1..8192. |

## Return value

- Type: `int`
- A context handle.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | A dimension is out of range. |
| `GLError` | No driver, no config, or the context cannot be made current. |

## Example

```tern
let surface = gl.create(640, 480);
```

## See also

- [gl.close](close.md)
- [gl.size](size.md)

---

Part of the [`gl`](index.md) namespace.
