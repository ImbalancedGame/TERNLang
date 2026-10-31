# d3d12.window

Opens a window to show frames in.

## Syntax

```tern
d3d12.window(width, height, title)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `width` | `int` | 1..16384; the client area, in pixels. |
| `height` | `int` | 1..16384. |
| `title` | `string` | The text in the title bar. |

## Return value

- Type: `int?`
- A window handle, or `nil` when this machine has no window layer.

## Remarks

Win32, loaded at run time. `nil` rather than an error is deliberate: a headless machine can still use the rest of this module, and a program that tries a window and falls back to files should not need a `try`. Windows only for now; on a system without `user32` it always answers `nil`. `TERN_USER32_LIBRARY`, `TERN_GDI32_LIBRARY` and `TERN_KERNEL32_LIBRARY` override the libraries, which is how the tests drive it.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | A dimension is out of range. |
| `TypeError` | The title is not a string. |

## Example

```tern
let window = d3d12.window(640, 480, "rotating cube");
```

## See also

- [d3d12.present](present.md)
- [d3d12.closeWindow](closeWindow.md)

---

Part of the [`d3d12`](index.md) namespace.
