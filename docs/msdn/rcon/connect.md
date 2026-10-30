# rcon.connect

Opens a session and authenticates.

## Syntax

```tern
rcon.connect(host, port, password, options?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `host` | `string` | The server. |
| `port` | `int` | The RCON port. |
| `password` | `string` | The RCON password. |
| `options` | `map` | Optional: `timeout`, milliseconds, default 5000. |

## Return value

- Type: `int`
- A session handle.

## Errors

| Error | Raised when |
| --- | --- |
| `AuthenticationError` | The password is refused. |
| `RconError` | The connection fails. |

## Example

```tern
let session = rcon.connect("127.0.0.1", 27015, "password");
```

## See also

- [rcon.execute](execute.md)
- [rcon.attach](attach.md)

---

Part of the [`rcon`](index.md) namespace.
