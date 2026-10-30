# sftp.download

Copies a file from the server.

## Syntax

```tern
sftp.download(remote, local, options?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `remote` | `string` | The source path. |
| `local` | `string` | The destination path. |
| `options` | `map` | As for `sftp.upload`. |

## Return value

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `SftpError` | The transfer fails. |

## Example

```tern
sftp.download("/var/log/app.log", "app.log", server);
```

## See also

- [sftp.upload](upload.md)

---

Part of the [`sftp`](index.md) namespace.
