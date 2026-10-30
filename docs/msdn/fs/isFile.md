# fs.isFile

True for a regular file.

## Syntax

```tern
fs.isFile(path)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | The path. |

## Return value

- Type: `bool`
- Whether it is a regular file.

## Example

```tern
println(fs.isFile("README.md"));
```

## See also

- [fs.isDir](isDir.md)

---

Part of the [`fs`](index.md) namespace.
