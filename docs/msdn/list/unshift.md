# list.unshift

Prepends values in place.

## Syntax

```tern
list.unshift(values...)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `values` | `any` | Values, inserted in the order given. |

## Return value

- Type: `int`
- The new length.

## Example

```tern
let xs = [3];
println(xs.unshift(1, 2), xs);
```

## See also

- [list.push](push.md)

---

Part of the [`list` methods](index.md).
