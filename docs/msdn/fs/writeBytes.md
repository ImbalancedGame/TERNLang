# fs.writeBytes

Writes a list of bytes, replacing the file and creating its directories.

## Syntax

```tern
fs.writeBytes(path, bytes)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | The file. |
| `bytes` | `list of int` | Values in 0..255. |

## Return value

- Type: `int`
- How many bytes were written.

## Remarks

`fs.write` takes text and writes its UTF-8 encoding; a string cannot hold a byte such as 0x89 at all, so a binary format has to go through this one. Bytes reach the disk unchanged, which is the other half of `fs.readBytes`.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The list holds something that is not a byte. |
| `IOError` | The file cannot be written. |

## Example

```tern
fs.writeBytes("logo.png", [137, 80, 78, 71, 13, 10, 26, 10]);
```

## See also

- [fs.write](write.md)
- [fs.readBytes](readBytes.md)
- [fs.appendBytes](appendBytes.md)

---

Part of the [`fs`](index.md) namespace.
