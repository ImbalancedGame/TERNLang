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

The same rules as `text.format`: `{}` takes the next argument and `{0}` indexes explicitly.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | A placeholder has no argument. |

## Example

```tern
println("{}: {}".format("port", 8080));
```

## See also

- [text.format](../text/format.md)

---

Part of the [`string` methods](index.md).
