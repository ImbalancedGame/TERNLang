# d3d12.present

Shows the target in the window.

## Syntax

```tern
d3d12.present(window, device)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `window` | `int` | A window handle. |
| `device` | `int` | A device handle whose target has been drawn to. |

## Return value

- Type: `bool`
- `false` once the window has been closed.

## Remarks

Presents through a flip-model DXGI swap chain when the machine has one: the frame is copied into a back buffer on the GPU and handed to the compositor, and `Present` waits for the vertical blank, which is what paces the loop. A machine without `IDXGIFactory2` - Windows 7, or a test run that refuses one - falls back to reading the target back and blitting it with `StretchDIBits`: slower, and the same picture. Escape or the close button ends the loop that calls it, and a closed window takes no more frames. `present` is also the call that keeps the window responsive, so a loop that stops calling it stops for good.

## Errors

| Error | Raised when |
| --- | --- |
| `D3D12Error` | There is no open window or device with that handle, or the blit's readback buffer cannot be mapped. |

## Example

```tern
let window = d3d12.window(640, 480, "cube");
while (d3d12.present(window, target)) {
    d3d12.clear(target, 0.05, 0.06, 0.09, 1);
}
```

## See also

- [d3d12.window](window.md)
- [d3d12.pixels](pixels.md)

---

Part of the [`d3d12`](index.md) namespace.
