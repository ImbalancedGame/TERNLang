# fs.seek

Moves a handle's position and returns it.

## Syntax

```tern
fs.seek(handle, offset)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `handle` | `int` | A handle from `fs.open`. |
| `offset` | `int` | The new byte offset. |

## Return value

- Type: `int`
- The position after seeking, which is the offset.

## Remarks

Seeking past the end is allowed; the next read returns `""`.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The offset is negative. |

## Example

```tern
fs.seek(file, 0);
```

## See also

- [fs.position](position.md)
- [fs.readChunk](readChunk.md)

---

Part of the [`fs`](index.md) namespace.
