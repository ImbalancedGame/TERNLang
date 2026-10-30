# list.indexOf

The index of the first occurrence.

## Syntax

```tern
list.indexOf(value)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `any` | The value. |

## Return value

- Type: `int`
- The index, or `-1`.

## Example

```tern
println([1, 2].indexOf(2));
```

## See also

- [list.lastIndexOf](lastIndexOf.md)
- [list.contains](contains.md)

---

Part of the [`list` methods](index.md).
