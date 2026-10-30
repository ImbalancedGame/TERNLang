# TERN native backend

`native/` compiles TERN to a standalone ELF binary. There is no interpreter and
no runtime library to install: a script becomes a small statically linked
executable that starts in about a millisecond.

```bash
./tools/tern emit app.tern -o app.json     # 1. bytecode -> JSON
python3 native/ternc.py app.json -o app.c  # 2. JSON -> C
cc -O2 -I native/runtime app.c -o app -lm  # 3. C -> ELF
./app
```

Step 2 and 3 are wrapped by `ternc.py` itself:

```bash
python3 native/ternc.py app.tern -o app           # whole pipeline
python3 native/ternc.py app.tern -o app --keep-c  # keep app.c next to the binary
python3 native/ternc.py app.tern -o app --no-traces  # ~10% faster, one-line errors
native/build.sh app.tern -o app                   # same thing, shell wrapper
```

## How it works

The interpreter's bytecode is a good compilation target: every instruction has
fixed width, jumps are absolute indices and locals are slot numbers. `ternc.py`
turns each instruction into a handful of C statements, so the C compiler does
the register allocation and instruction selection:

| Bytecode             | Generated C                                              |
| -------------------- | -------------------------------------------------------- |
| one prototype        | one `static Value fn_N(Closure *, int argc, Value *)`     |
| instruction *i*      | label `L<i>` (jumps become `goto L<i>`)                   |
| locals               | `Value lc[locals]` (captured ones live on the heap)       |
| operand stack        | `Value st[stack]` + `int sp`, as in the VM                |
| `AddLocalConstStore` | `__builtin_add_overflow` fast path, `tern_add` fallback   |
| `ModLocalConstMagic` | 64x64->128 multiply + shift + exact range check           |
| `JumpLtLocalInt`     | the comparison inlined, no boxing                         |

The runtime (`native/runtime/tern_rt.{h,c}`) is included into the same
translation unit, so helper functions such as `tern_add` and `tern_get_member`
inline into the generated code. Values are 16 bytes: an 8-byte payload (int,
double or object pointer) and a 1-byte tag.

Other notable points:

* **Closures** — captured locals are heap cells (`Cell *`), so `CloseUpvalues`
  is a no-op and a closure can outlive its frame.
* **Structs** — `NewInstance` and calling a type value both go through
  `tern_new_instance`, which copies the per-field defaults (including defaults
  that need to be evaluated per instance).
* **Exceptions** — `EnterTry` installs a `setjmp` frame on a global handler
  stack; `Throw` (and every runtime error) `longjmp`s to the innermost one.
  Functions that contain a `try` block keep their frame in memory (`volatile`)
  so a resume cannot lose it, and `return` inside `try` restores the handler
  stack.
* **Stack traces** — the export carries a line per instruction (`lines`), and
  `ternc.py` stores the current line wherever it changes, so a binary can say
  where an error happened. Each closure call pushes a `TernFrame` on its own C
  stack and links it into a thread (`tern_frame_cur`); the trace is walked
  innermost first and capped at 32 entries, exactly like the interpreter, so an
  uncaught error prints the same `at fn (file:line)` list on both back ends.
  `try` handlers remember the frame pointer they were installed under and
  restore it, so a caught error never leaves a stale frame behind. Compiling
  with `--no-traces` turns the push into a no-op (and leaves the line table
  alone, so errors still name their line).
* **Modules** — each imported file gets its own `G_N` globals array and a
  `load_module_N()` that runs its body once and returns a map of its exports.
  `import "std:x"` constants are resolved against the runtime's namespaces.
* **Allocation and GC** — objects come from 16-byte size classes (16..512
  bytes) with one free list per class, bump-allocated out of 256 KiB blocks;
  larger objects get a block of their own. A mark-sweep collector runs when 16
  MiB or twice the live set has been allocated since the last collection. Roots
  are the registered global/constant/type arrays plus the C stack and
  callee-saved registers, scanned conservatively (a word that points at an
  object start keeps that object alive), so generated functions do not have to
  describe their frames. `TERN_GC_THRESHOLD=<bytes>` overrides the initial
  threshold; a tiny value (e.g. `512`) collects constantly and is how the
  collector is stress-tested.
* **Standard library** — the whole standard library lives in the runtime: the
  globals, `math`/`time`/`json`/`os`/`random`/`text`/`lists`/`fs`, the string,
  list, map, range and number methods, and map insertion order. `fs` is backed
  by `stat`/`opendir` and raises the interpreter's catchable `IOError` for
  anything missing; its namespaces hold exactly the members `std:*` modules
  expose, so a name cannot resolve to the wrong function. `tests/17_stdlib`
  exercises every namespace, method and conversion and its output must match
  the interpreter byte for byte, so a builtin that exists on one side and not
  the other shows up as a failing test rather than a surprise at run time.
* **Arguments** — `main` records `argv` minus the program name, which is what
  `os.args()` returns; passing arguments works exactly as it does under
  `tern run script.tern`.
* **Text** — strings are UTF-8 bytes, but every position the language exposes
  (length, index, slice, iteration, `chars`, `indexOf`, `ord`, pad widths) is
  counted the way the interpreter counts it: in UTF-16 code units, so
  `len("héllo")` is 5 and `len("日本語")` is 3. Individual characters are moved
  whole, so `"日本語".reversed()` gives `"語本日"` rather than shredded bytes,
  and case conversion uses the C library's Unicode tables (`setlocale` is set
  to `C.UTF-8` at start-up), so `"héllo".upper()` is `"HÉLLO"`. The code-unit
  count is cached in the string object, so `len()` is O(1) after the first call.

## Tests

```bash
python3 native/run_tests.py        # all tests/*.tern, compiled natively
python3 native/run_tests.py 05 14  # a subset
python3 native/parity.py           # tests, examples and sweep*.tern, diffed
                                   # against `tern run` instead of a golden
```

Every golden test except `12_lint` (which checks compiler *diagnostics*, not
runtime behaviour) is compiled and run, and its output compared with
`tests/NN_name.out`. Each one then runs a second time with
`TERN_GC_THRESHOLD=4096` so the collector is under constant pressure:

```
01_basics       ok     07_match        ok     14_modulo       ok     20_traces       ok
02_strings      ok     08_errors       ok     15_stores       ok     21_depth        ok
03_control      ok     09_builtins     ok     16_gc           ok     22_fs           ok
04_functions    ok     10_modules_main ok     17_stdlib       ok     23_methods      ok
05_collections  ok     11_operators    ok     18_patterns     ok     25_numbers      ok
06_structs      ok     13_loops        ok     19_unicode      ok
```

`parity.py` is the differential version of the same idea: it compiles every
script (the tests, the examples, and the `sweep*.tern` scripts, which call every
documented method, namespace member, global and operator and print `repr` or
`ERR`) and diffs the
combined output against `./tools/tern run` with no golden file in sight. That is
how the method-table, namespace-collision and stepped-range differences between
the two back ends were found; when in doubt about a builtin, add a line to
`sweep.tern`, and for an operator to `sweep_ops.tern`. Both sides run in a scratch directory, so a script that writes
files cannot disturb the working tree.

The five `bench/*.tern` workloads produce byte-identical output to
`./tools/tern run` as well.

## Benchmarks

`python3 native/bench.py --interp --runs 9` compiles each workload and times it
against the interpreter on the same 2 vCPU machine. Native times include
process start-up (~1 ms); interpreter times include the .NET start-up (~90 ms),
because that is what a user pays to run the script:

| Workload   | native | `tern run` | speed-up |
| ---------- | -----: | ---------: | -------: |
| `fib`      |   20 ms |     257 ms |   13.0x |
| `loop`     |   56 ms |     264 ms |    4.7x |
| `maps`     |  122 ms |     312 ms |    2.6x |
| `strings`  |    7 ms |     141 ms |   20.6x |
| `bintree`  |    8 ms |     158 ms |   19.3x |

(Timings move by up to 20% with host load; these were taken on an otherwise
idle 2 vCPU machine.)

Measured in-process instead (start-up excluded, best of nine), the interpreter
needs 134 / 113 / 192 / 20 / 51 ms for the same five workloads, so the compiled
code is **6.7x** ahead on `fib`, 2.0x on `loop`, 1.6x on `maps`, 2.9x on
`strings` and 6.4x on `bintree` — and within 2.5x of LuaJIT on `fib` and
`bintree`, while staying a plain C compiler back end.

Collecting costs nothing here: `bintree`, `fib` and `loop` stay under the 16 MiB
allocation budget and never collect at all, and `maps` (the only workload that
crosses it) pays a few percent. Run any binary with `TERN_GC_STATS=1` to see
collections, bytes allocated and bytes live on exit.

Compiling costs: export 80 ms, code generation 35 ms, `cc -O2` about 1.8 s for
the whole single-translation-unit program.

## Networking

`std:net` works in compiled binaries with the same API and, more importantly,
the same wire format as the interpreter ([`TERN/Builtins/Wire.cs`](../TERN/Builtins/Wire.cs)),
so a compiled server talks to an interpreted client and the other way round. The
format is a tag byte per value (`0` nil, `1` false, `2` true, `3` int, `4` float,
`5` string, `6` list, `7` map, `8` struct, `9` range), ULEB128 lengths and
counts, little-endian 64-bit numbers, and a 4-byte big-endian frame length.
Struct values carry their type name and field names; the receiver looks the type
up among its own globals and falls back to a plain map when it has not declared
it.

The rest of the networking stack is mirrored too: `net` handles frame limits,
connect timeouts, a token handshake, raw byte I/O and UDP datagrams; `std:http`,
`std:rcon` and `std:sftp` are byte-for-byte the same API. TLS is the one place
where the two back ends use different libraries — `SslStream` in the interpreter
and the system OpenSSL here — so `libssl` is loaded with `dlopen` at the first
TLS connection instead of being linked. A machine without it still builds and
runs scripts; only `{"tls": true}` fails, with `TLS support is not available on
this system`. SFTP runs the OpenSSH `sftp` client in batch mode from a
`fork`/`exec` pair, so it needs `sftp` on the PATH and key-based authentication.

## Limitations

* The collector is conservative: an integer that happens to look like an object
  address can keep one object alive, and the heap floor is 16 MiB before the
  first collection, so peak RSS for small programs is the same as before.
* Allocation is a free-list pop (small) or a `malloc` per large object, which
  costs a few percent on allocator-heavy workloads compared with the old bump
  allocator.
* Characters above U+FFFF (emoji, rare CJK) count as two code units for `len`,
  like the interpreter, but indexing, slicing, `chars()` and iteration treat
  one as a single character: the runtime moves whole code points, whereas the
  interpreter splits UTF-16 surrogate pairs. Text at or below U+FFFF — Latin,
  Greek, Cyrillic, CJK — behaves identically.
* Compiler diagnostics (the `12_lint` warnings) are not emitted by binaries;
  use `./tools/tern check` for that.
* There is no sandbox: `--no-fs`, `--no-net`, `engine.AllowFileSystem` and
  `engine.AllowNetwork` belong to the interpreter, because a compiled binary has
  no engine to configure. Restrict a binary with the operating system (user,
  container, `chroot`, firewall) instead.
* `fs` errors name the path as the script wrote it, while the interpreter
  reports the resolved absolute path.
* Stack traces cost about a cycle per call: pushing and popping a frame is
  three stores, and `fib(30)` — 2.7 M calls — runs 2-3% slower than with
  `--no-traces`, which also drops the per-line stores. Errors then still print
  a single `at <module> (file:line)` line, pointing at the failing instruction.
* Every builtin checks its argument count: the 25 globals, the per-type method
  tables and all 74 namespace members (`math.floor`, `fs.read`, ...) carry the
  interpreter's min/max contract and raise its `ArgumentError` in its wording
  ("len() expects 1 argument(s) but got 2", "push() expects at least 1
  argument(s) but got 0", "clamp() expects 3 argument(s) but got 2"). Missing
  arguments are therefore an error instead of a `nil` that crashes inside the
  builtin.
* Error text matches the interpreter, JSON syntax errors included: the native
  parser reports the same sentence at the same offset ("expected a string key
  at offset 1", "invalid escape '\q'"). Documents nesting more than 512 levels
  are refused by both back ends instead of overflowing the stack, and a
  surrogate pair in a `\u` escape decodes to one code point while a lone
  surrogate becomes U+FFFD.
* Recursion is limited to 10 000 frames, the same as the interpreter. The limit
  is high enough to stay inside the C stack (~3 MB per thread) and raises a
  catchable `StackOverflow` error rather than crashing.

## Relationship to the interpreter

The interpreter (`TERN/`) stays the reference implementation: it is what the
compiler tests, the REPL, the linter and `tern run` use. The native backend
consumes exactly the bytecode the interpreter runs, so any difference in
behaviour is a backend bug — `native/run_tests.py` exists to catch those.
