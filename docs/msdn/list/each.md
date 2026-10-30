# list.each

Calls a callback for each item.

## Syntax

```tern
list.each(callback)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `callback` | `function` | Called with each item. |

## Return value

`nil`.

## Remarks

The callback's results are discarded; use `map` to collect them. Removing an item during the iteration shifts the rest, so collect first and remove after.

## Example

```tern
[1, 2].each(fn (x) { println(x); });
```

## See also

- [list.map](map.md)

---

Part of the [`list` methods](index.md).
