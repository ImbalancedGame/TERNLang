# fs.open

Opens a file for reading and returns a handle that keeps its position.

## Syntax

```tern
fs.open(path)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | The file. |

## Return value

- Type: `int`
- A handle for `fs.readChunk`, `fs.seek`, `fs.position`, `fs.fileSize` and `fs.close`.

## Remarks

The handle is independent of the file's size: opening a four-gigabyte archive costs one call and allocates nothing until you read.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The file cannot be opened, or 64 handles are already open. |

## Example

```tern
let file = fs.open("huge.log");
```

## See also

- [fs.readChunk](readChunk.md)
- [fs.readAt](readAt.md)
- [fs.close](close.md)

---

Part of the [`fs`](index.md) namespace.
