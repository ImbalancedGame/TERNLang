# json.stringify

Serialises a value as JSON.

## Syntax

```tern
json.stringify(value, indent?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `value` | `any` | A value made of maps, lists, strings, numbers, booleans and nil. |
| `indent` | `int` | Optional spaces per level; compact when omitted. |

## Return value

- Type: `string`
- The JSON text.

## Remarks

Keys are written in insertion order, and strings are escaped according to RFC 8259. A float that is whole is written without a fractional part, which means it reads back as an integer.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The value contains something JSON cannot hold. |

## Example

```tern
println(json.stringify({"a": [1, 2]}, 2));
```

## See also

- [json.parse](parse.md)

---

Part of the [`json`](index.md) namespace.
