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

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The file cannot be written. |

## Example

```tern
d3d12.save(target, "frame.png");
```

## See also

- [d3d12.pixels](pixels.md)

---

Part of the [`d3d12`](index.md) namespace.
