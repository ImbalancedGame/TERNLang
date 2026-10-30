# fs.size

Size of a file in bytes.

## Syntax

```tern
fs.size(path)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | The file. |

## Return value

- Type: `int`
- The size.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The file cannot be statted. |

## Example

```tern
println(fs.size("app.tar.gz"));
```

## See also

- [fs.fileSize](fileSize.md)
- [fs.readAt](readAt.md)

---

Part of the [`fs`](index.md) namespace.
