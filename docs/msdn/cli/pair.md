# cli.pair

Splits "320x240" into [320, 240].

## Syntax

```tern
cli.pair(text, name?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `text` | `string` | Two whole numbers around a lowercase `x`, such as `320x240`. |
| `name` | `string, optional` | The option this came from, used in the error message: `'320' for --size is not a size`. |

## Return value

- Type: `list of int`
- `[width, height]`.

## Remarks

The message is the point. `int(parts[0])` on `--size 320x` raises a conversion
error that does not mention the option, the value, or the shape wanted;
`cli.pair` does all three, and rejects `0` and negatives, which pass `int` and
then make a renderer fail in a way that names nothing.

## Errors

| Error | Raised when |
| --- | --- |
| `UsageError` | The text is not two positive whole numbers around an `x`. The message shows the accepted form. |

## Example

```tern
import "std:cli";

let options = cli.parse({"size": "320x240"});
let size = cli.pair(options["size"], "--size");
println(size[0] * size[1] + " pixels");
```

## See also

- [cli.parse](parse.md)

---

Part of the [`cli`](index.md) namespace.
