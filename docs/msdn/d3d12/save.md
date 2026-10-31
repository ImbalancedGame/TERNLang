# d3d12.save

Saves the target as a PNG.

## Syntax

```tern
d3d12.save(device, path)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `device` | `int` | A device handle. |
| `path` | `string` | Where to write it. |

## Return value

- Type: `int`
- The number of bytes written.

## Remarks

The byte count is the writer's own evidence that the picture reached the file. A PNG of a 640x480 frame is a few hundred kilobytes, so any other number is worth looking at. `d3d12.pixels` returns the same image as a list of ints if you want to look at it without a file.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The file cannot be written. |

## Example

```tern
let device = d3d12.create(640, 480);
d3d12.draw(device);
println("saved", d3d12.save(device, "frame.png"), "bytes");
```

## See also

- [d3d12.pixels](pixels.md)

---

Part of the [`d3d12`](index.md) namespace.
