# float.round

Rounds to the nearest integer, or to n digits.

## Syntax

```tern
float.round(digits?)
```

`receiver` is the float on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `digits` | `int` | Optional; with it the result is a float. |

## Return value

- Type: `float`
- The rounded value, as a float.

## Example

```tern
println(2.5.round(), 2.567.round(1));
```

## See also

- [math.round](../math/round.md)

---

Part of the [`float` methods](index.md).
