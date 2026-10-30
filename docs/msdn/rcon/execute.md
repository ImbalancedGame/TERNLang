# rcon.execute

Runs one command and returns everything the server sent.

## Syntax

```tern
rcon.execute(session, command)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `session` | `int` | An RCON session. |
| `command` | `string` | The command line. |

## Return value

- Type: `string`
- The response text, joined across packets.

## Errors

| Error | Raised when |
| --- | --- |
| `RconError` | The exchange fails or times out. |

## Example

```tern
println(rcon.execute(session, "status"));
```

## See also

- [rcon.connect](connect.md)

---

Part of the [`rcon`](index.md) namespace.
