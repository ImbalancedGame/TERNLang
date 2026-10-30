# fs.fileSize

Size of an open file in bytes.

## Syntax

```tern
fs.fileSize(handle)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `handle` | `int` | A handle from `fs.open`. |

## Return value

- Type: `int`
- The size.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The handle is not open. |

## Example

```tern
println(fs.fileSize(file));
```

## See also

- [fs.size](size.md)

---

Part of the [`fs`](index.md) namespace.
