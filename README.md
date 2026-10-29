# TERN

TERN is a small, fast scripting language for people who support software:
engineers, SREs and support professionals who need to automate a job *now*
without dragging a compiler toolchain behind them. It has a modern,
expression-oriented syntax, compiles to bytecode, and runs on a stack virtual
machine written in C#.

TERN 2.x is a ground-up rewrite of the original TERN interpreter. The old
`scope X { Method { arg0 } }` / `@If(...)` syntax is gone; see
[Migrating from TERN 1.x](#migrating-from-tern-1x).

```tern
struct Ticket { id, title, severity = "minor"

    fn summary() { return "#${this.id} ${this.title} [${this.severity}]"; }
}

let tickets = [
    Ticket(1, "disk full", "critical"),
    Ticket(2, "slow login"),
    Ticket(3, "typo on the dashboard"),
];

for ticket in tickets.filter(fn(t) => t.severity == "critical") {
    println("page on-call: ${ticket.summary()}");
}

match tickets.length {
    0       => println("queue is clear"),
    1..9    => println("normal load"),
    _       => println("escalate"),
}
```

## Features

* **Modern syntax** — `let`/`const`, functions with defaults and `...rest`,
  arrow lambdas, block scoping, string interpolation, ternaries.
* **Real data structures** — lists, insertion-ordered maps, lazy ranges, and
  user-defined structs with methods and defaults.
* **Pattern matching** — literals, ranges, or-patterns, guards, and list
  patterns with `...rest`, usable as an expression.
* **Errors as values** — `try` / `catch` / `finally` with `throw`, plus
  catchable runtime errors and stack traces with line numbers.
* **Batteries included** — `math`, `json`, `fs`, `os`, `time`, `random`,
  `text`, `lists` namespaces and rich methods on every built-in type.
* **Modules** — `import "lib/util.tern"` for files, `import "std:json"` for
  standard modules, with per-engine caching.
* **A single dependency-free binary** — a bytecode VM with no runtime
  reflection in the hot path.
* **Sandboxable** — file system access can be disabled per engine, so scripts
  from a ticket or a chat bot can be run safely.
* **Linted on the way in** — a fast AST pass warns about assignments used as
  conditions, impossible comparisons and shadowed locals before anything runs.

## Quick start

```bash
# one-time toolchain bootstrap (fetches Roslyn + a private .NET runtime)
./tools/setup-toolchain.sh

# build the VM and CLI
./tools/build.py

# run something
./tools/tern run examples/triage.tern

# other commands
./tools/tern check examples/triage.tern   # compile only, report diagnostics and warnings
./tools/tern disasm examples/triage.tern  # dump bytecode
./tools/tern repl                         # interactive (:help for commands)
./tools/tern docs                         # standard library reference (markdown)
./tools/tern version
```

Script arguments are exposed as `os.args()`:

```bash
./tools/tern run examples/wordcount.tern notes.txt
```

Add `--time` to `run` for a wall-clock measurement and `--no-fs` to run with the
file system sandboxed.

## Language tour

Full reference: [`docs/language.md`](docs/language.md) for the language and
[`docs/stdlib.md`](docs/stdlib.md) for every builtin, generated from the code
with `tern docs`.

### Values

`int` · `float` · `string` · `bool` · `nil` · `list` · `map` · `range` ·
`function` · struct instances. Integers stay integers, division promotes to
float only when it is inexact (`7 / 2` is `3.5`, `8 / 2` is `4`), and
`1 == 1.0` is `true`.

### Strings

```tern
let name = "tern";
println("hello, ${name.upper()}!");         // hello, TERN!
println("a-b-c".split("-").join(" + "));    // a + b + c
println("abc"[1..<3]);                      // bc
println("x".padStart(3, "."));              // ..x
```

### Collections

```tern
let xs = [3, 1, 2];
xs.sort();                       // in place, stable
xs.map(fn(v) => v * 2);          // [6, 2, 4]
xs.filter(fn(v) => v > 1);       // [3, 2]
xs.reduce(fn(acc, v) => acc + v, 0);

let counts = {};
counts["a"] = 1;
counts.has("a");                 // true
for key, value in counts { println("${key}=${value}"); }

2 in xs;                         // true
len(0..<5);                      // 5, a range is lazy
```

### Functions and closures

```tern
fn greet(name, greeting = "hello", ...rest) {
    return "${greeting}, ${name} (${rest.length})";
}

fn counter() {
    let n = 0;
    return fn() { n++; return n; };
}
```

### Structs

```tern
struct Stack {
    items = []                     // defaults run per instance

    fn push(v) { this.items.push(v); return this; }
    fn size() { return this.items.length; }
}

let s = Stack().push(1).push(2);
println(s.size());                  // 2
```

### Errors

```tern
try {
    let value = json.parse(payload);
} catch (e) {
    println("bad payload: ${e}");
} finally {
    println("done");
}
```

Uncaught errors print a message plus a stack trace and exit with status 1:

```
TypeError: nil has no method 'missing'
  at inner (app:1)
  at outer (app:2)
  at <module> (app:3)
```

## Performance

`tools/compare.sh` runs five workloads written identically in TERN, Lua 5.4,
LuaJIT, Node 22 and C# (Roslyn) and compares in-process timings, so startup and
host warm-up are excluded. Best of seven runs on a 2 vCPU / 2.6 GHz machine:

| Workload   | TERN | Lua 5.4 | LuaJIT | Node 22 | C#    | TERN / Lua |
| ---------- | ---: | ------: | -----: | ------: | ----: | ---------: |
| `fib`      |  113 |      51 |      6 |       8 |     5 |      2.21x |
| `loop`     |  101 |      30 |     12 |       8 |     4 |      3.37x |
| `maps`     |  204 |     140 |     55 |      86 |    61 |      1.45x |
| `bintree`  |   33 |      26 |      6 |       4 |    22 |      1.25x |
| `strings`  |   16 |      10 |      5 |       2 |     4 |      1.54x |

Whole-process times for the equivalent scripts (`./tools/bench.sh`, including
~86 ms of process start, parse, compile and JIT):

| Benchmark      | Workload                                   | Time      |
| -------------- | ------------------------------------------ | --------- |
| `fib.tern`     | `fib(30)`, 2.7 M calls                     | ~170 ms   |
| `loop.tern`    | 5 M iteration numeric loop                 | ~194 ms   |
| `maps.tern`    | 200 k string-keyed inserts + 200 k lookups | ~258 ms   |
| `bintree.tern` | 65 535-node struct tree, alloc heavy       | ~76 ms    |
| `strings.tern` | 20 k interpolations + join/split/upper     | ~61 ms    |

TERN is a bytecode interpreter, so the honest comparison is against Lua 5.4;
LuaJIT, Node and C# are tracing/AOT compilers and are 20-30x ahead on numeric
code. See `bench/compare/workloads.md` for the full breakdown and the remaining
per-opcode costs.

The optimisations that get there:

* **Tagged `Value` structs** — numbers and booleans never box inside the
  interpreter loop.
* **A single dispatch loop** over a contiguous `Instr[]`, with the whole
  instruction (opcode plus three operands) in one 16-byte struct and a
  `[MethodImpl(AggressiveOptimization)]` entry point so the interpreter is fully
  optimised from its first call while everything else stays quick-jitted for a
  fast start.
* **Fused arithmetic, compare and store opcodes** — `AddLocalLocal`,
  `AddLocalConst`, `AddLocalConstStore`, `AddLocalStack`, `JumpLtLocalInt`, ...,
  so `total += i % 7` compiles to three instructions.
* **Counted loops** — `ForPrep{Lt,Le,Gt,Ge}` / `ForNext{Lt,Le,Gt,Ge}` fold the
  increment, the comparison and the branch into one opcode per iteration, and
  update the counter in place.
* **Reciprocal modulo** — `x % k` for a positive constant `k` becomes a
  64x64→128 multiply and a shift, with an exact range check that falls back to
  the hardware divide.
* **Cheap calls** — pooled pre-populated frames, per-frame stack reservation, an
  unrolled argument copy for the first nine arities, and inline return paths.
* **Constant folding** in the compiler, including integer `**`.
* **Locals in a flat slot array** — parameters, receivers and loop counters are
  index-addressed, never looked up by name at runtime.
* **Jump targets patched at emit time**, so there is no runtime branch
  resolution.
* **String keys hashed without allocation** in the map implementation.

## Repository layout

```
TERN/               the language: lexer, parser, compiler, bytecode, VM, builtins
  Syntax/           Lexer.cs, Ast.cs, Parser.cs
  Compiler/         Bytecode.cs, Compiler.cs, Disassembler.cs
  Runtime/          Value.cs, Objects.cs, VM.cs
  Builtins/         BuiltinMethods.cs, StandardLibrary.cs
  TernEngine.cs     embeddable engine: modules, compilation, sandboxing
  Diagnostics.cs    diagnostics and TernError
TernRunner/         the `tern` CLI (run / check / repl / disasm / version)
tools/              build.py, tern launcher, test.sh, bench.sh, gen-docs.sh,
                    setup-toolchain.sh
tests/              golden-output test suite (NN_name.tern + NN_name.out)
examples/           runnable sample scripts
bench/              micro benchmarks
docs/language.md    the language reference
docs/stdlib.md      generated builtin reference (`tools/gen-docs.sh`)
```

## Embedding

```csharp
using TERN;
using TERN.Runtime;

// The standard library is installed by the constructor.
var engine = new TernEngine(includeStandardLibrary: true);
engine.AllowFileSystem = false;           // sandbox: no file system access
engine.ScriptArguments.Add("notes.txt");

// Run source directly...
var value = engine.RunScript("""println("hi from TERN");  1 + 2""", "inline");

// ...or a file, then read the value its top level produced.
var module = engine.LoadFile("script.tern");
var result = engine.Execute(module);

// Expose host functions to scripts.
engine.RegisterFunction("log", (vm, args) =>
{
    Console.WriteLine(args[0].ToDisplayString());
    return Value.Nil;
}, minArguments: 1, maxArguments: 1, documentation: "Writes a line to the host log.");
```

Errors surfacing from TERN are `TernError` values carrying the message, the
error type and a `Trace` of `(function, line)` frames, so a host can render
them however it likes instead of parsing console output.

The VM also enforces resource ceilings, configurable through
`engine.Options`: `MaxInstructions` (default 2e9), `MaxCallDepth` (1024),
`MaxCollectionSize` (8 M elements) and `MaxStringLength` (16 M chars). They
turn runaway scripts into catchable errors instead of host OOM crashes.

## Development

```bash
./tools/build.py          # compile everything (add --debug for debug info)
./tools/test.sh           # run the golden test suite
./tools/test.sh --update  # re-freeze expected outputs after intentional changes
./tools/bench.sh          # run the benchmarks
./tools/gen-docs.sh       # regenerate docs/stdlib.md from the engine
```

The suite is plain TERN: each `tests/NN_name.tern` is run and compared against
`tests/NN_name.out`, so adding a test means adding one file and freezing its
output. `tools/test.sh` also fails when `docs/stdlib.md` drifts from the
registered standard library.

## Migrating from TERN 1.x

TERN 2.x is not backward compatible. The table maps the old constructs:

| TERN 1.x                        | TERN 2.x                            |
| ------------------------------- | ----------------------------------- |
| `scope Console { ... }`         | plain top-level functions, or a module |
| `Method { arg0 arg1 }`          | `fn method(a, b) { ... }`           |
| `arg0 .. argN`                  | named parameters                    |
| `@If(cond) { }`                 | `if cond { }`                       |
| `@For` / `@Foreach`             | `for item in list { }`              |
| string `+` only                 | interpolation: `"${value}"`         |
| errors as console text          | `try` / `catch` / `finally`         |

## License

See the repository for licensing information.
