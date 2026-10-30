# string.startsWith

True when the string begins with the prefix.

## Syntax

```tern
string.startsWith(prefix)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `prefix` | `string` | The prefix. |

## Return value

- Type: `bool`
- Whether it does.

## Example

```tern
println("hello".startsWith("he"));
```

## See also

- [string.endsWith](endsWith.md)

---

Part of the [`string` methods](index.md).
