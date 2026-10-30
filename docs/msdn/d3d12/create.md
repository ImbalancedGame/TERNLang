# d3d12.create

Creates a device and an offscreen render target.

## Syntax

```tern
d3d12.create(width, height)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `width` | `int` | 1..16384. |
| `height` | `int` | 1..16384. |

## Return value

- Type: `int`
- A device handle.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | A dimension is out of range. |
| `D3D12Error` | The device, target or readback buffer cannot be created. |

## Example

```tern
let target = d3d12.create(640, 480);
```

## See also

- [d3d12.close](close.md)
- [d3d12.size](size.md)

---

Part of the [`d3d12`](index.md) namespace.
