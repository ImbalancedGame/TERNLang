# list.push

Appends values in place.

## Syntax

```tern
list.push(values...)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `values` | `any` | One or more values to append. |

## Return value

- Type: `int`
- The new length.

## Remarks

Mutating the receiver is what distinguishes `push` from `concat`; the return value is the length, not the list, so chain further work separately.

## Example

```tern
let xs = [1];
println(xs.push(2, 3), xs);
```

## See also

- [list.unshift](unshift.md)
- [list.pop](pop.md)
- [list.concat](concat.md)

---

Part of the [`list` methods](index.md).
