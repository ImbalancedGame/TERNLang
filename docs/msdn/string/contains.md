# string.contains

True when the substring occurs.

## Syntax

```tern
string.contains(substring)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `substring` | `string` | The text to look for. |

## Return value

- Type: `bool`
- Whether it occurs.

## Remarks

The empty substring occurs everywhere, so this returns true.

## Example

```tern
println("hello".contains("ell"), "hello".contains("z"));
```

## See also

- [string.indexOf](indexOf.md)
- [string.startsWith](startsWith.md)

---

Part of the [`string` methods](index.md).
