# ord

The code point of a string's first character.

## Syntax

```tern
ord(text)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `text` | `string` | A non-empty string. |

## Return value

- Type: `int`
- The code point.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The string is empty. |

## Example

```tern
println(ord("A"), ord("☃"));
```

## See also

- [chr](chr.md)

---

Part of the [`global`](index.md) namespace.
