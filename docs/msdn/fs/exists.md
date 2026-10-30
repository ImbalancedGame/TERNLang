# fs.exists

True when a file or directory exists.

## Syntax

```tern
fs.exists(path)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | The path. |

## Return value

- Type: `bool`
- Whether it exists.

## Example

```tern
println(fs.exists("build"));
```

## See also

- [fs.isFile](isFile.md)
- [fs.isDir](isDir.md)

---

Part of the [`fs`](index.md) namespace.
