# string.endsWith

True when the string ends with the suffix.

## Syntax

```tern
string.endsWith(suffix)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `suffix` | `string` | The suffix. |

## Return value

- Type: `bool`
- Whether it does.

## Example

```tern
println("hello".endsWith("lo"));
```

## See also

- [string.startsWith](startsWith.md)

---

Part of the [`string` methods](index.md).
