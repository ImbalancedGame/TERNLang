# fs.close

Closes a file handle.

## Syntax

```tern
fs.close(handle)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `handle` | `int` | A handle from `fs.open`. |

## Return value

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The handle is not open, including a second close. |

## Example

```tern
fs.close(file);
```

## See also

- [fs.open](open.md)

---

Part of the [`fs`](index.md) namespace.
