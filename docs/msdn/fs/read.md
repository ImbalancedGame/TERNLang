# fs.read

Reads a whole file as text.

## Syntax

```tern
fs.read(path)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | The file. |

## Return value

- Type: `string`
- The contents.

## Remarks

For a file that may be large, use `fs.open` and `fs.readChunk` instead: this one loads everything.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The file cannot be read. |
| `TypeError` | The path is not a string. |
| `PermissionError` | File system access is disabled for this script. |

## Example

```tern
println(fs.read("notes.txt"));
```

## See also

- [fs.readChunk](readChunk.md)
- [fs.readAt](readAt.md)
- [fs.readBytes](readBytes.md)

---

Part of the [`fs`](index.md) namespace.
