# text.format

Fills placeholders with arguments.

## Syntax

```tern
text.format(template, values...)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `template` | `string` | Text with `{}` or `{0}` placeholders. |
| `values` | `any` | The values to substitute. |

## Return value

- Type: `string`
- The filled text.

## Remarks

`{}` consumes the next argument; `{0}`, `{1}` index explicitly and may repeat. `{{` and `}}` write literal braces. Values are rendered with `str`.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | A placeholder has no matching argument, or the index is out of range. |

## Example

```tern
println(text.format("{} + {} = {}", 1, 2, 3));
println(text.format("{1} {0}", "a", "b"));
```

## See also

- [string.format](../string/format.md)
- [global.str](../global/str.md)

---

Part of the [`text`](index.md) namespace.
