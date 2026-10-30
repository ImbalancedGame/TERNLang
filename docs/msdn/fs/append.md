# fs.append

Appends text, creating the file if it is missing.

## Syntax

```tern
fs.append(path, text)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | The file. |
| `text` | `any` | Rendered with `str`. |

## Return value

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The file cannot be written. |

## Example

```tern
fs.append("log.txt", "one line\n");
```

## See also

- [fs.write](write.md)

---

Part of the [`fs`](index.md) namespace.
