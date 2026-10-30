# sftp.run

Runs batch commands and reports the outcome without raising.

## Syntax

```tern
sftp.run(commands, options)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `commands` | `list of string` | Commands for the `sftp` batch parser. |
| `options` | `map` | As for `sftp.upload`. |

## Return value

- Type: `map`
- `status` (exit code), `output`, `error`.

## Remarks

`upload`, `download` and `list` are built on this; they raise where `run` returns. A non-zero status is data here, not an exception.

## Errors

| Error | Raised when |
| --- | --- |
| `SftpError` | A command contains a line break. |

## Example

```tern
let report = sftp.run(["ls -1 /"], server);
println(report["status"]);
```

## See also

- [sftp.batch](batch.md)
- [sftp.command](command.md)

---

Part of the [`sftp`](index.md) namespace.
