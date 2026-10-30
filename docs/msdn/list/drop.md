# list.drop

A copy without the first n items.

## Syntax

```tern
list.drop(count)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `count` | `int` | How many to drop. |

## Return value

- Type: `list`
- The copy.

## Example

```tern
println([1, 2, 3].drop(2));
```

## See also

- [list.take](take.md)

---

Part of the [`list` methods](index.md).
