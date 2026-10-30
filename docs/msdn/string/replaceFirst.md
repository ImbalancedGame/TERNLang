# string.replaceFirst

Replaces the first occurrence of a substring.

## Syntax

```tern
string.replaceFirst(find, with)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `find` | `string` | What to replace. |
| `with` | `string` | The replacement. |

## Return value

- Type: `string`
- The new string, unchanged when `find` is absent.

## Example

```tern
println("a-b-c".replaceFirst("-", " "));
```

## See also

- [string.replace](replace.md)

---

Part of the [`string` methods](index.md).
