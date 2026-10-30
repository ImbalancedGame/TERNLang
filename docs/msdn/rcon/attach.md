# rcon.attach

Takes over a connection as an RCON session.

## Syntax

```tern
rcon.attach(socket, password, options?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `socket` | `int` | A connection from `net.connect`. |
| `password` | `string` | The RCON password. |
| `options` | `map` | Optional: `timeout`. |

## Return value

- Type: `int`
- The same handle, now an RCON session.

## Remarks

After attaching, `net.send` on the handle reports `needs a connection handle, not an rcon session` — the handle belongs to `rcon` now.

## Errors

| Error | Raised when |
| --- | --- |
| `NetError` | The handle is not a plain connection. |
| `AuthenticationError` | The password is refused; the socket is left open. |

## Example

```tern
let session = rcon.attach(net.connect("127.0.0.1", 27015), "password");
```

## See also

- [rcon.connect](connect.md)
- [rcon.execute](execute.md)

---

Part of the [`rcon`](index.md) namespace.
