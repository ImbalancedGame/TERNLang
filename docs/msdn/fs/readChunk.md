# fs.readChunk

Reads up to n bytes from a handle's position and advances it.

## Syntax

```tern
fs.readChunk(handle, length)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `handle` | `int` | A handle from `fs.open`. |
| `length` | `int` | How many bytes to ask for, 1..1073741824. |

## Return value

- Type: `string`
- The bytes read; fewer than asked at the end of the file, and `""` at EOF.

## Remarks

A chunk that would end inside a multi-byte character is extended by the two or three bytes the character still needs, so chunks concatenate back into the file and nothing is decoded twice. Reading at EOF returns `""` rather than raising, which is what ends a streaming loop.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The handle is not open. |
| `ArgumentError` | `length` is out of range. |

## Example

```tern
let file = fs.open("huge.log");
let chunk = fs.readChunk(file, 4096);
while (chunk != "") { process(chunk); chunk = fs.readChunk(file, 4096); }
```

## See also

- [fs.readAt](readAt.md)
- [fs.position](position.md)
- [fs.seek](seek.md)

---

Part of the [`fs`](index.md) namespace.
