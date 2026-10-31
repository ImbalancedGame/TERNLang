# d3d12.info

Describes the adapter and the binding.

## Syntax

```tern
d3d12.info()
```

## Parameters

_None._

## Return value

- Type: `map`
- `name`, `nodes` and `build`; `build` is the revision of the Direct3D 12 binding, which a failure report should quote.

## Errors

| Error | Raised when |
| --- | --- |
| `D3D12Error` | The library is not available. |

## Example

```tern
println(d3d12.info()["name"]);
```

## See also

- [d3d12.available](available.md)

---

Part of the [`d3d12`](index.md) namespace.
