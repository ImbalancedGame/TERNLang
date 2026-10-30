# d3d12.close

Releases the device and everything it owns.

## Syntax

```tern
d3d12.close(device)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `device` | `int` | A device handle. |

## Return value

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `D3D12Error` | There is no open device with that handle. |

## Example

```tern
d3d12.close(target);
```

## See also

- [d3d12.create](create.md)

---

Part of the [`d3d12`](index.md) namespace.
