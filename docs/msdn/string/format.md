# string.format

Formats the string as a template.

## Syntax

```tern
string.format(values...)
```

`receiver` is the string on the left of the dot.

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `values` | `any` | Values for the placeholders. |

## Return value

- Type: `string`
- The filled text.

## Remarks

The same rules as `text.format`: `{}` takes the next argument, `{0}` indexes explicitly, and either may carry a specification such as `{:>8.2f}`.

## Errors

| Error | Raised when |
| --- | --- |
| `FormatError` | A placeholder has no argument, or its specification is not one. |
| `TypeError` | A numeric type was given something that is not a number. |

## Example

```tern
println("{:>6}: {:08.2f}".format("port", 8080));
```

## See also

- [text.format](../text/format.md)

---

Part of the [`string` methods](index.md).
