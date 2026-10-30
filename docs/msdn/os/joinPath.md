# os.joinPath

Joins path segments with the platform separator.

## Syntax

```tern
os.joinPath(segments...)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `segments` | `string` | One or more segments. |

## Return value

- Type: `string`
- The joined path.

## Remarks

No normalisation: `..` is kept and an absolute segment resets the path. Uses `/` on POSIX and `\` on Windows.

## Example

```tern
println(os.joinPath("a", "b", "c.txt"));
```

## See also

- [fs.read](../fs/read.md)

---

Part of the [`os`](index.md) namespace.
