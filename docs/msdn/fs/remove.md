# fs.remove

Deletes a file or a directory tree.

## Syntax

```tern
fs.remove(path)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | The path; a missing path is not an error. |

## Return value

`nil`.

## Example

```tern
fs.remove("build");
```

## See also

- [fs.mkdir](mkdir.md)

---

Part of the [`fs`](index.md) namespace.
