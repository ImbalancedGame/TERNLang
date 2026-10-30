# fs.mkdir

Creates a directory and its parents.

## Syntax

```tern
fs.mkdir(path)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `path` | `string` | The directory. |

## Return value

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The directory cannot be created. |

## Example

```tern
fs.mkdir("build/out");
```

## See also

- [fs.remove](remove.md)

---

Part of the [`fs`](index.md) namespace.
