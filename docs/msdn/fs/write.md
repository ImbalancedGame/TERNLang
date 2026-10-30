# fs.write

Writes text, replacing the file and creating its directories.

## Syntax

```tern
fs.write(path, text)
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
fs.write("out/report.txt", "done");
```

## See also

- [fs.append](append.md)

---

Part of the [`fs`](index.md) namespace.
