# error

Raises an error the script can catch.

## Syntax

```tern
error(message)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `message` | `any` | The message; rendered with `str`. |

## Return value

`nil`.

## Remarks

The error's name is `Error`. Use it for a condition the caller is expected to handle; `panic` is for one they are not.

## Errors

| Error | Raised when |
| --- | --- |
| `Error` | Always: this is the only thing it does. |

## Example

```tern
try { error("bad input"); } catch (e) { println("caught:", e); }
```

## See also

- [panic](panic.md)
- [assert](assert.md)

---

Part of the [`global`](index.md) namespace.
