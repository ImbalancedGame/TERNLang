# len

Counts the items in a string, list, map or range.

## Syntax

```tern
len(value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `string\|list\|map\|range` | The value. |

## Return value

- Type: `int`
- The length.

## Remarks

For a string this counts UTF-16 code units, which is what an index means too: ASCII text counts one per character, and a code point above U+FFFF counts two. `chars()` splits by character, so `len(s.chars())` is the number of characters and `len(s)` is the number of indices.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The value has no length. |

## Example

```tern
println(len("héllo"), len([1, 2]), len({"a": 1}), len(0..<5));
```

## See also

- [string.count](../string/count.md)
- [list.isEmpty](../list/isEmpty.md)

---

Part of the [`global`](index.md) namespace.
