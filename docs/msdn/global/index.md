# global

The functions that need no import.

These are bound in every script, module and REPL session. They cover conversion,
output, reflection and the two ways to fail.

Names are not keywords: a `let` may shadow `len`. A shadowed global is still
reachable through nothing else, so shadowing one is usually a mistake.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`abs`](abs.md) | 1..1 | The absolute value. |
| [`assert`](assert.md) | 1..2 | Raises unless the condition is truthy. |
| [`bool`](bool.md) | 1..1 | Converts a value to a boolean. |
| [`chr`](chr.md) | 1..1 | The string for a Unicode code point. |
| [`copy`](copy.md) | 1..1 | A shallow copy of a list or a map. |
| [`eprint`](eprint.md) | 0..n | Writes its arguments to standard error. |
| [`error`](error.md) | 1..1 | Raises an error the script can catch. |
| [`float`](float.md) | 1..1 | Converts a value to a float. |
| [`has`](has.md) | 2..2 | True when a map has a key. |
| [`input`](input.md) | 0..1 | Reads one line from standard input. |
| [`int`](int.md) | 1..2 | Converts a value to an integer. |
| [`keys`](keys.md) | 1..1 | The keys of a map, in insertion order. |
| [`len`](len.md) | 1..1 | Counts the items in a string, list, map or range. |
| [`max`](max.md) | 1..n | The largest of its arguments. |
| [`min`](min.md) | 1..n | The smallest of its arguments. |
| [`ord`](ord.md) | 1..1 | The code point of a string's first character. |
| [`panic`](panic.md) | 0..1 | Aborts the script with an uncaught error. |
| [`print`](print.md) | 0..n | Writes its arguments to standard output, separated by a space. |
| [`println`](println.md) | 0..n | Writes its arguments and a newline. |
| [`range`](range.md) | 1..3 | Creates an integer range. |
| [`repr`](repr.md) | 1..1 | Converts a value to a debug representation. |
| [`sleep`](sleep.md) | 1..1 | Blocks for a number of milliseconds. |
| [`str`](str.md) | 1..1 | Converts a value to its string form. |
| [`type`](type.md) | 1..1 | The type name of a value. |
| [`values`](values.md) | 1..1 | The values of a map, in insertion order. |

## Example

```tern
println("items:", len([1, 2, 3]), type(1.5));
assert(len("abc") == 3, "length");
```

