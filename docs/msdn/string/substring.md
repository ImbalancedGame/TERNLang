# string.substring

A substring between two indices.

## Syntax

```tern
string.substring(start, end?)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `start` | `int` | The first index; negative counts from the end. |
| `end` | `int` | Optional; the end index (exclusive), the length by default. |

## Return value

- Type: `string`
- The substring.

## Remarks

The second argument is an end index, not a length: `"hello".substring(1, 3)` is `"el"`, the same as `slice` with the same bounds. The name is familiar from other languages where it means a length, which is the trap.

## Example

```tern
println("hello".substring(1, 3));
```

## See also

- [string.slice](slice.md)

---

Part of the [`string` methods](index.md).
