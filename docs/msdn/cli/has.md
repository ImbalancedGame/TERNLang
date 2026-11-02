# cli.has

True when the argument was typed at all.

## Syntax

```tern
cli.has(name)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `name` | `string` | The option name, without the leading dashes. |

## Return value

- Type: `bool`
- True when `--name` or `--name=...` appears in the arguments.

## Remarks

A default is not the same thing as a request. `--frames 12` and no `--frames` at
all leave the same value in the parsed map, and only `cli.has("frames")` tells
them apart - which is what a program needs to decide whether the user asked for
something or merely failed to.

## Example

```tern
import "std:cli";

let options = cli.parse({"frames": 12});
if (cli.has("frames")) { println("asked for " + str(options["frames"])); }
```

## See also

- [cli.parse](parse.md)

---

Part of the [`cli`](index.md) namespace.
