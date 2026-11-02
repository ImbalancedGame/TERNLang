# cli

Command-line options, read against a schema of defaults.

A program that takes options needs three things: to read them, to say what it
does with them, and to say no politely to something it does not understand.
`cli.parse` is all three. It takes the option names with their defaults and
their types baked in - a `false` default is a switch, a number default takes a
number, anything else takes text - then reads `os.args()`:

- `--out x.png` and `--out=x.png` both work.
- `--ascii` sets true and never swallows the next word; `--ascii=false` sets false.
- A number that is not a number is refused, naming the option and the shape it wants.
- An unknown option is refused with the nearest declared name when there is one.
- `--help` (or `-h`) prints a table of the options and their defaults, and
  `parse` answers `nil`, which is why every program that calls it starts with
  `if (options == nil) { return; }`.
- `--` ends the options; everything after it is positional.
- Anything left over is reachable from `cli.args()`.

The optional second argument is a map from option name to a one-line
description, which is all `--help` needs to print a usage screen worth reading.
Without it the help is still correct, just terser.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`args`](args.md) | 0..0 | The arguments that were not options, from the last parse. |
| [`has`](has.md) | 1..1 | True when the argument was typed at all. |
| [`pair`](pair.md) | 1..2 | Splits "320x240" into [320, 240]. |
| [`parse`](parse.md) | 1..2 | Reads the arguments against a schema of defaults; nil after printing --help. |

## Example

```tern
import "std:cli";

let options = cli.parse({
    "out": "picture.png",
    "size": "320x240",
    "samples": 1,
    "ascii": false,
}, {
    "out": "where the PNG goes",
    "size": "WIDTHxHEIGHT",
    "samples": "rays per pixel",
    "ascii": "print a text preview as well",
});
if (options == nil) { return; }   // --help already printed

let size = cli.pair(options["size"], "--size");
println(options["out"], size[0], size[1], options["samples"], options["ascii"]);
if (cli.has("samples")) { println("samples was typed"); }
```

