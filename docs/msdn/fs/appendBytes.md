# fs.appendBytes

Appends a list of bytes to a file, creating it if it is missing.

## Syntax

```tern
fs.appendBytes(path, bytes)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | The file. |
| `bytes` | `list of int` | Values in 0..255. |

## Return value

- Type: `int`
- How many bytes were appended.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The list holds something that is not a byte. |
| `IOError` | The file cannot be written. |

## Example

```tern
fs.appendBytes("frames.bin", [0, 161, 0, 0]);
```

## See also

- [fs.writeBytes](writeBytes.md)
- [fs.append](append.md)

---

Part of the [`fs`](index.md) namespace.
