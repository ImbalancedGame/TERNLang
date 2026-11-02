# cli.args

The arguments that were not options, from the last parse.

## Syntax

```tern
cli.args()
```

## Parameters

_None._

## Return value

- Type: `list of string`
- The positional arguments, in the order they were typed. Empty before any call to `cli.parse`.

## Remarks

Options and positionals can be mixed: `--out x.png one.png two.png` leaves
`["one.png", "two.png"]` here. Everything after `--` is positional even if it
starts with a dash.

## Example

```tern
import "std:cli";

let options = cli.parse({"out": "a.png"});
if (options == nil) { return; }
for path in cli.args() { println(path); }
```

## See also

- [cli.parse](parse.md)

---

Part of the [`cli`](index.md) namespace.
