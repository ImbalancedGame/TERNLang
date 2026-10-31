# d3d12.debug

Turns the Direct3D 12 debug layer on, and says whether it is running.

## Syntax

```tern
d3d12.debug(enabled?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `enabled` | `bool` | Optional. `true` turns validation on and `false` turns it off; omit it to ask without changing anything. |

## Return value

- Type: `bool`
- Whether validation is on. `false` means this machine has no debug layer (Windows Settings > Apps > Optional features > Graphics Tools), and the reason is printed once to stderr.

## Remarks

The layer has to be on before the device is created: `d3d12.debug(true)` then `d3d12.create`, in that order. It makes the driver print exactly which call was illegal and why, which turns "the device was removed for an unknown reason" into something that can be fixed. It is off by default because it slows rendering down and is not installed on every machine.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The argument is not `true` or `false`. |

## Example

```tern
d3d12.debug(true);
let target = d3d12.create(640, 480);
```

## See also

- [d3d12.create](create.md)
- [d3d12.info](info.md)

---

Part of the [`d3d12`](index.md) namespace.
