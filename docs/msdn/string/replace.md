# string.replace

Replaces every occurrence of a substring.

## Syntax

```tern
string.replace(find, with)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `find` | `string` | What to replace. |
| `with` | `string` | The replacement. |

## Return value

- Type: `string`
- The new string.

## Remarks

Matches do not overlap, so replacing `aa` with `b` in `aaaa` gives `bb`.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | `find` is empty. |

## Example

```tern
println("a-b-c".replace("-", " "));
```

## See also

- [string.replaceFirst](replaceFirst.md)

---

Part of the [`string` methods](index.md).
