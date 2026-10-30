# list.contains

True when a value is present.

## Syntax

```tern
list.contains(value)
```

`receiver` is the list on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `any` | The value, compared with `==`. |

## Return value

- Type: `bool`
- Whether it is present.

## Remarks

A linear scan: for a membership test inside a loop over large data, keep a map's keys instead.

## Example

```tern
println([1, 2].contains(2));
```

## See also

- [list.indexOf](indexOf.md)
- [map.has](../map/has.md)

---

Part of the [`list` methods](index.md).
