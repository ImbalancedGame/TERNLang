# gl.close

Destroys a context and its programs.

## Syntax

```tern
gl.close(context)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `context` | `int` | A context handle. |

## Return value

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `GLError` | There is no open context with that handle. |

## Example

```tern
gl.close(surface);
```

## See also

- [gl.create](create.md)

---

Part of the [`gl`](index.md) namespace.
