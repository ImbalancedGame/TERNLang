# list.removeValue

Removes the first occurrence of a value.

## Syntax

```tern
list.removeValue(value)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `any` | The value, compared with `==`. |

## Return value

- Type: `bool`
- Whether an item was removed.

## Example

```tern
let xs = [1, 2, 2];
println(xs.removeValue(2), xs);
```

## See also

- [list.remove](remove.md)
- [list.indexOf](indexOf.md)

---

Part of the [`list` methods](index.md).
