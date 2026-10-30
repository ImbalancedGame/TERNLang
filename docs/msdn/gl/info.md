# gl.info

Describes the driver.

## Syntax

```tern
gl.info()
```

## Parameters

_None._

## Return value

- Type: `map`
- `vendor`, `renderer`, `version`, `glsl`.

## Errors

| Error | Raised when |
| --- | --- |
| `GLError` | No driver is available. |

## Example

```tern
println(gl.info()["renderer"]);
```

## See also

- [gl.available](available.md)

---

Part of the [`gl`](index.md) namespace.
