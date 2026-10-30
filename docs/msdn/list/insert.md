# list.insert

Inserts an item at an index.

## Syntax

```tern
list.insert(index, value)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `index` | `int` | Where it goes; the length is allowed, negatives are not. |
| `value` | `any` | The item. |

## Return value

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `IndexError` | The index is negative or beyond the end. |

## Example

```tern
let xs = [1, 3];
xs.insert(1, 2);
println(xs);
```

## See also

- [list.push](push.md)

---

Part of the [`list` methods](index.md).
