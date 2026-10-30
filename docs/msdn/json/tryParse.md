# json.tryParse

Parses, returning nil for bad input.

## Syntax

```tern
json.tryParse(text, fallback?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `text` | `string` | The document. |
| `fallback` | `any` | Optional; `nil` by default. |

## Return value

- Type: `any`
- The parsed value, or the fallback.

## Remarks

For input from a network or a file that may be something else entirely.

## Example

```tern
println(json.tryParse("{", "default"));
```

## See also

- [json.parse](parse.md)

---

Part of the [`json`](index.md) namespace.
