# fs.list

Directory entries as a list of names.

## Syntax

```tern
fs.list(path)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | The directory. |

## Return value

- Type: `list of string`
- Names, sorted.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The directory cannot be read. |

## Example

```tern
println(fs.list("tests"));
```

## See also

- [fs.isDir](isDir.md)

---

Part of the [`fs`](index.md) namespace.
