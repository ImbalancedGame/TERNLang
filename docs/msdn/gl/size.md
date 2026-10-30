# gl.size

The size of a context.

## Syntax

```tern
gl.size(context)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `context` | `int` | A context handle. |

## Return value

- Type: `list of int`
- `[width, height]`.

## Example

```tern
println(gl.size(surface));
```

## See also

- [gl.create](create.md)

---

Part of the [`gl`](index.md) namespace.
