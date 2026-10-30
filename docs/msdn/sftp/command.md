# sftp.command

The command line `sftp` is started with.

## Syntax

```tern
sftp.command(options)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `options` | `map` | As for `sftp.upload`. |

## Return value

- Type: `list of string`
- The argv, program name first.

## Remarks

`-P` is always passed, `-i` only when `key` is set, and the batch file is fed on standard input (`-b -`) so no temporary file is written.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | `host` or `user` is missing, or an option is out of range. |

## Example

```tern
println(sftp.command({"host": "h", "user": "u"}));
```

## See also

- [sftp.batch](batch.md)

---

Part of the [`sftp`](index.md) namespace.
