# json.parse

Parses JSON text into values.

## Syntax

```tern
json.parse(text)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `text` | `string` | The document. |

## Return value

- Type: `any`
- A map, list, string, number, boolean or `nil`.

## Remarks

A number written with a fraction or exponent becomes a float; anything else becomes an integer. A duplicate key replaces the earlier value and keeps its position, like `map.set`.

## Errors

| Error | Raised when |
| --- | --- |
| `JsonError` | The text is not JSON; the message gives the line and column. |

## Example

```tern
println(json.parse("{\"a\": 1}")["a"]);
```

## See also

- [json.tryParse](tryParse.md)
- [json.stringify](stringify.md)

---

Part of the [`json`](index.md) namespace.
