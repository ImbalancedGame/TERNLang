# panic

Aborts the script with an uncaught error.

## Syntax

```tern
panic(message?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `message` | `any` | Optional text. |

## Return value

`nil`.

## Remarks

A panic is still catchable from TERN code — the difference from `error` is that a panic means the program cannot continue. An uncaught one exits with status 1 and a stack trace on standard error.

## Errors

| Error | Raised when |
| --- | --- |
| `Panic` | Always. |

## Example

```tern
try { panic("unreachable"); } catch (e) { println(e); }
```

## See also

- [error](error.md)
- [assert](assert.md)

---

Part of the [`global`](index.md) namespace.
