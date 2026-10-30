# list.flatten

Flattens nested lists.

## Syntax

```tern
list.flatten(depth?)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `depth` | `int` | Optional, 1 by default; how many levels to open. |

## Return value

- Type: `list`
- The flattened copy.

## Remarks

Only lists are opened: a map that happens to contain lists stays as it is.

## Example

```tern
println([1, [2, [3]]].flatten(2));
```

## See also

- [list.concat](concat.md)

---

Part of the [`list` methods](index.md).
