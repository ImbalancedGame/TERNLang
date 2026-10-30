# string.toFloat

Parses the string as a float.

## Syntax

```tern
string.toFloat(fallback?)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `fallback` | `any` | Optional; returned instead of raising. |

## Return value

- Type: `float|any`
- The value, or the fallback.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The text is not a number and no fallback was given. |

## Example

```tern
println("2.5".toFloat() + 1.0);
```

## See also

- [string.toInt](toInt.md)

---

Part of the [`string` methods](index.md).
