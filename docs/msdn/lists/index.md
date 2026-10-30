# lists

Whole-list helpers that do not belong on a receiver.

The methods on a list cover the common cases; these are the operations that read
better as a function — building a list, pairing two lists, or sorting without
touching the original.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`chunk`](chunk.md) | 2..2 | Splits a list into lists of at most n items. |
| [`enumerate`](enumerate.md) | 1..1 | Index and value pairs. |
| [`range`](range.md) | 1..3 | A list of integers from start to end. |
| [`repeat`](repeat.md) | 2..2 | A list with one value repeated. |
| [`sort`](sort.md) | 1..2 | A sorted copy, leaving the original alone. |
| [`zip`](zip.md) | 1..n | Pairs the items of several lists. |

## Example

```tern
import "std:lists";

println(lists.zip([1, 2], ["a", "b"]));
println(lists.chunk([1, 2, 3, 4, 5], 2));
```

