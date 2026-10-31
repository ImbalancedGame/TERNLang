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

Copies the target back, blits it into the window, processes the window's messages and paces the frame at about 60 per second. Escape or the close button ends the loop that calls it. This is not a DXGI swap chain: a frame round-trips through the readback buffer, which is slower than presenting properly but needs no presentation surface. `present` is also the call that keeps the window responsive, so a loop that stops calling it stops for good.

## Errors

| Error | Raised when |
| --- | --- |
| `D3D12Error` | There is no open window or device with that handle, or the readback buffer cannot be mapped. |

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
