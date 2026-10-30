# sftp.upload

Copies a local file to the server.

## Syntax

```tern
sftp.upload(local, remote, options?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `local` | `string` | A local path that must exist. |
| `remote` | `string` | The destination path. |
| `options` | `map` | `host`, `user`, and optionally `key`, `port`, `timeout`, `binary`. |

## Return value

`nil`.

## Remarks

The error message carries the first line of `sftp`'s own error output, so `Permission denied (publickey)` reaches the script unchanged. The `binary` option names the program to run — the string `sftp` by default — which is how the tests substitute a stand-in.

## Errors

| Error | Raised when |
| --- | --- |
| `SftpError` | The local file is missing, the key cannot be read, or `sftp` exits non-zero. |

## Example

```tern
sftp.upload("build/app.tar.gz", "/srv/app.tar.gz", {"host": "example.com", "user": "deploy"});
```

## See also

- [sftp.download](download.md)
- [sftp.list](list.md)

---

Part of the [`sftp`](index.md) namespace.
