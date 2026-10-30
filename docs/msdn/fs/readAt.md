# fs.readAt

Reads up to n bytes at a byte offset, without a handle.

## Syntax

```tern
fs.readAt(path, offset, length?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | The file. |
| `offset` | `int` | The byte offset to start at. |
| `length` | `int` | Optional; the rest of the file by default, capped at 1 GiB. |

## Return value

- Type: `string`
- The bytes read, `""` past the end of the file.

## Remarks

Random access: reading the header of a large archive costs one seek and the bytes actually returned.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The file cannot be opened. |
| `ArgumentError` | The offset is negative or the length is out of range. |

## Example

```tern
println(fs.readAt("huge.log", 512 * 1024 * 1024, 100));
```

## See also

- [fs.readChunk](readChunk.md)
- [fs.seek](seek.md)

---

Part of the [`fs`](index.md) namespace.
