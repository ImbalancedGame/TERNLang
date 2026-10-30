# int.times

Calls a callback n times.

## Syntax

```tern
int.times(callback)
```

`receiver` is the int on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `callback` | `function` | Called with the index 0..n-1. |

## Return value

`nil`.

## Remarks

The counted loop without a range in the way, and the usual way to build a list of n things.

## Example

```tern
3.times(fn (i) { println(i); });
```

## See also

- [range.each](../range/each.md)
- [lists.repeat](../lists/repeat.md)

---

Part of the [`int` methods](index.md).
