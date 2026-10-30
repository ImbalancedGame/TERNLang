# rcon.close

Closes a session.

## Syntax

```tern
rcon.close(session)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `session` | `int` | An RCON session. |

## Return value

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `NetError` | The handle is not an RCON session. |

## Example

```tern
rcon.close(session);
```

## See also

- [rcon.connect](connect.md)

---

Part of the [`rcon`](index.md) namespace.
