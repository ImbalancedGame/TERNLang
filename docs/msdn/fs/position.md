# fs.position

The byte offset the next read starts at.

## Syntax

```tern
fs.position(handle)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `handle` | `int` | A handle from `fs.open`. |

## Return value

- Type: `int`
- The position.

## Remarks

Save this if you need to resume reading later: it is where the reader got to.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The handle is not open. |

## Example

```tern
let resumeFrom = fs.position(file);
```

## See also

- [fs.seek](seek.md)
- [fs.readChunk](readChunk.md)

---

Part of the [`fs`](index.md) namespace.
