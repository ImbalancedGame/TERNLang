# string.toInt

Parses the string as an integer.

## Syntax

```tern
string.toInt(fallback?)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `fallback` | `any` | Optional; returned instead of raising. |

## Return value

- Type: `int|any`
- The value, or the fallback.

## Remarks

The whole string must parse after trimming: `"12abc"` fails where `" 12 "` succeeds.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The text is not an integer and no fallback was given. |

## Example

```tern
println("42".toInt(), "x".toInt(-1));
```

## See also

- [string.toFloat](toFloat.md)
- [global.int](../global/int.md)

---

Part of the [`string` methods](index.md).
