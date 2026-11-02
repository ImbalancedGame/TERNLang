# cli.parse

Reads the arguments against a schema of defaults; nil after printing --help.

## Syntax

```tern
cli.parse(schema, notes?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `schema` | `map` | Option name to default value. The default's type is the option's type: `false` is a switch, a number takes a number, anything else takes text. |
| `notes` | `map, optional` | Option name to a one-line description, printed beside the option by `--help`. |

## Return value

- Type: `map or nil`
- Every declared option, typed or defaulted. `nil` when `--help` was asked for and the table has been printed.

## Remarks

The whole parse is strict: a typo is an error rather than a silent default, because the
alternative is a program that runs with the wrong settings and says nothing. The error
message lists the options it does know, so the fix is in the message.

A switch never consumes the next argument, so `--ascii --out x.png` and
`--ascii out.png` both leave `out.png` positional. `--ascii=false` turns one off
explicitly. `--` stops option parsing, which is how a file called `--out` is passed.

## Errors

| Error | Raised when |
| --- | --- |
| `UsageError` | An unknown option, a missing value, or a value whose text is not a number. The message names the option and, for a near-miss, suggests the declared one. |
| `TypeError` | The first argument is not a map. |

## Example

```tern
import "std:cli";

let options = cli.parse({"out": "a.png", "samples": 1, "ascii": false});
if (options == nil) { return; }
println(options["out"], options["samples"], options["ascii"]);
```

## See also

- [cli.args](args.md)
- [cli.has](has.md)
- [cli.pair](pair.md)
- [os.args](../os/args.md)

---

Part of the [`cli`](index.md) namespace.
