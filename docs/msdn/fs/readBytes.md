# fs.readBytes

Reads a whole file as a list of bytes.

## Syntax

```tern
fs.readBytes(path)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | The file. |

## Return value

- Type: `list of int`
- The bytes.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The file cannot be read. |

## Example

```tern
println(fs.readBytes("logo.png")[0..4]);
```

## See also

- [fs.read](read.md)
- [fs.readAt](readAt.md)

---

Part of the [`fs`](index.md) namespace.
