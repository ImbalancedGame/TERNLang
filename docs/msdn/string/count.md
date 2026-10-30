# string.count

Counts non-overlapping occurrences of a substring.

## Syntax

```tern
string.count(substring)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `substring` | `string` | The text to count. |

## Return value

- Type: `int`
- How many times it occurs.

## Remarks

Occurrences do not overlap: `"aaa".count("aa")` is 1, not 2. An empty substring counts 0.

## Example

```tern
println("banana".count("an"));
```

## See also

- [len](len.md)
- [string.indexOf](indexOf.md)

---

Part of the [`string` methods](index.md).
