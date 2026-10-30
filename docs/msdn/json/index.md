# json

Reading and writing JSON.

`json.parse` produces the obvious values: objects become maps, arrays become
lists, numbers become integers when they are written without a fraction or
exponent and floats otherwise, and `null` becomes `nil`.

`json.stringify` accepts maps, lists, strings, numbers, booleans and `nil`.
Anything else — a function, a handle — is reported rather than silently dropped.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`parse`](parse.md) | 1..1 | Parses JSON text into values. |
| [`stringify`](stringify.md) | 1..2 | Serialises a value as JSON. |
| [`tryParse`](tryParse.md) | 1..2 | Parses, returning nil for bad input. |

## Example

```tern
import "std:json";

let value = json.parse("{\"a\": [1, 2, 3]}");
println(value["a"][1]);
println(json.stringify(value, 2));
```

