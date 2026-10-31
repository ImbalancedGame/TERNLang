# text

Text helpers that read better as functions.

The same operations exist as methods on a string; `text.format` is the one with
no method equivalent that matters, and `text.join` is the function form of
`list.join` for code that already has a list in hand.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`format`](format.md) | 1..n | Fills placeholders with arguments. |
| [`fromBytes`](fromBytes.md) | 1..3 | Decodes a list of bytes as UTF-8 text. |
| [`join`](join.md) | 1..2 | Joins a list with a separator. |
| [`lower`](lower.md) | 1..1 | A lowercased copy. |
| [`pad`](pad.md) | 2..3 | Pads text to a width on the right. |
| [`split`](split.md) | 2..2 | Splits text on a separator. |
| [`toBytes`](toBytes.md) | 1..1 | Encodes text as UTF-8 bytes. |
| [`trim`](trim.md) | 1..1 | Text without surrounding whitespace. |
| [`upper`](upper.md) | 1..1 | An uppercased copy. |

## Example

```tern
import "std:text";

println(text.format("{} of {}", 3, 10));
println(text.join(["a", "b"], "-"));
```

