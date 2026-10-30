# string.repeat

The string repeated n times.

## Syntax

```tern
string.repeat(count)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `count` | `int` | How many copies; 0 gives the empty string. |

## Return value

- Type: `string`
- The repeated string.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | `count` is negative. |

## Example

```tern
println("ab".repeat(3));
```

## See also

- [string.padStart](padStart.md)

---

Part of the [`string` methods](index.md).
