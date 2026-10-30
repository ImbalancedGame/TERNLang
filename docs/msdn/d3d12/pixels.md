# d3d12.pixels

Reads the target back as RGBA bytes.

## Syntax

```tern
d3d12.pixels(device)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `device` | `int` | A device handle. |

## Return value

- Type: `list of int`
- `width * height * 4` bytes, bottom row first, like `gl.pixels`.

## Errors

| Error | Raised when |
| --- | --- |
| `D3D12Error` | The readback buffer cannot be mapped. |

## Example

```tern
println(d3d12.pixels(target)[0..4]);
```

## See also

- [d3d12.save](save.md)

---

Part of the [`d3d12`](index.md) namespace.
