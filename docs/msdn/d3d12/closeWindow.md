# d3d12.closeWindow

Closes a window.

## Syntax

```tern
d3d12.closeWindow(window)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `window` | `int` | A window handle. |

## Return value

`nil`.

## Remarks

Destroys the window and forgets the handle. A window that the user has already closed still has to be released this way, or its slot stays taken.

## Errors

| Error | Raised when |
| --- | --- |
| `D3D12Error` | There is no open window with that handle. |

## Example

```tern
d3d12.closeWindow(window);
```

## See also

- [d3d12.window](window.md)

---

Part of the [`d3d12`](index.md) namespace.
