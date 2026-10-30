# sftp.list

Lists a remote directory.

## Syntax

```tern
sftp.list(remote, options?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `remote` | `string` | The directory. |
| `options` | `map` | As for `sftp.upload`. |

## Return value

- Type: `list of string`
- Entry names, one per line of `ls -1`.

## Errors

| Error | Raised when |
| --- | --- |
| `SftpError` | The listing fails. |

## Example

```tern
println(sftp.list("/srv", server));
```

## See also

- [sftp.upload](upload.md)

---

Part of the [`sftp`](index.md) namespace.
