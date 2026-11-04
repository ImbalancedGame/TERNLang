# d3d12.input

The mouse and keyboard since the last call.

## Syntax

```tern
d3d12.input(window)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `window` | `int` | A window handle. |

## Return value

- Type: `map`
- `dx`, `dy`, `wheel`, `left`, `right`, `x`, `y` and `keys`.

## Remarks

The map has one entry per thing an orbit camera needs:

| Key | Type | Meaning |
| --- | ---- | ------- |
| `dx`, `dy` | int | Pixels the pointer moved while a button was held. |
| `wheel` | int | Notches turned, positive away from the user. |
| `left`, `right` | bool | Whether that button is down now. |
| `x`, `y` | int | The pointer, in client pixels, y down. |
| `keys` | list of int | Virtual-key codes pressed since the last call. |

*`dx`, `dy`, `wheel` and `keys` are consumed by the call*: a second `input` with no messages in between answers zeroes and an empty list, so a loop that reads them twice does not turn the camera twice. The button state and the pointer are not consumed, because they describe the world rather than an event.

The drag is measured from the button press, not from wherever the pointer happened to be, so the first move after a click is small and the camera does not jump. A drag that leaves the window still counts: the window takes the mouse capture on the press and gives it back on the release. Auto-repeat is filtered out, so holding a key gives one event, not thirty a second - at most sixteen keys are kept per call.

The messages are collected by `d3d12.present`, which pumps the queue; reading `input` without presenting returns whatever the last present collected, which on a fresh window is nothing.

## Errors

| Error | Raised when |
| --- | --- |
| `D3D12Error` | There is no open window with that handle. |

## Example

```tern
let input = d3d12.input(window);
if (input["left"]) {
    yaw = yaw + input["dx"] * 0.01;
}
```

## See also

- [d3d12.window](window.md)
- [d3d12.present](present.md)

---

Part of the [`d3d12`](index.md) namespace.
