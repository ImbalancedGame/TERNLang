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
python3 native/ternc.py app.tern -o app          # whole pipeline
python3 native/ternc.py app.tern -o app --keep-c # keep app.c next to the binary
native/build.sh app.tern -o app                  # same thing, shell wrapper
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
  stack. Uncaught errors print `Kind: message` and exit non-zero.
* **Modules** — each imported file gets its own `G_N` globals array and a
  `load_module_N()` that runs its body once and returns a map of its exports.
  `import "std:x"` constants are resolved against the runtime's namespaces.
* **Allocation** — objects are immortal (no collector yet) and bump-allocated
  out of 1 MiB blocks, one pointer bump per object.
* **Standard library** — the whole standard library lives in the runtime: the
  globals, `math`/`time`/`json`/`os`/`random`/`text`/`lists`, the string, list,
  map, range and number methods, and map insertion order.

## Tests

```bash
python3 native/run_tests.py        # all tests/*.tern, compiled natively
python3 native/run_tests.py 05 14  # a subset
```

Every golden test except `12_lint` (which checks compiler *diagnostics*, not
runtime behaviour) is compiled and run, and its output compared with
`tests/NN_name.out`:

```
01_basics       ok     05_collections  ok     09_builtins     ok     13_loops        ok
02_strings      ok     06_structs      ok     10_modules_main ok     14_modulo       ok
03_control      ok     07_match        ok     11_operators    ok     15_stores       ok
04_functions    ok     08_errors       ok
```

The four `examples/*.tern` scripts and the five `bench/*.tern` workloads also
produce byte-identical output to `./tools/tern run`.

## Benchmarks

`python3 native/bench.py --interp --runs 9` compiles each workload and times it
against the interpreter on the same 2 vCPU machine. Native times include
process start-up (~1 ms); interpreter times include the .NET start-up (~90 ms),
because that is what a user pays to run the script:

| Workload   | native | `tern run` | speed-up |
| ---------- | -----: | ---------: | -------: |
| `fib`      |   21 ms |     280 ms |   13.0x |
| `loop`     |   73 ms |     272 ms |    3.7x |
| `maps`     |  146 ms |     317 ms |    2.2x |
| `strings`  |    6 ms |     147 ms |   21.4x |
| `bintree`  |    6 ms |     174 ms |   26.6x |

Measured in-process instead (start-up excluded, best of nine, from
`tools/compare.sh`), the interpreter needs 157 / 104 / 148 / 18 / 45 ms for the
same five workloads, so the compiled code is **7.4x** ahead on `fib`, 1.4x on
`loop`, level on `maps`, 2.6x on `strings` and 7.4x on `bintree` — and within
2-3x of LuaJIT and Node on `fib` and `bintree` while staying a plain C compiler
back end.

Compiling costs: export 80 ms, code generation 35 ms, `cc -O2` about 1.8 s for
the whole single-translation-unit program.

## Limitations

* No garbage collector yet: memory is never reclaimed, so long-running programs
  that allocate in a loop will grow.
* Compiler diagnostics (the `12_lint` warnings) are not emitted by binaries;
  use `./tools/tern check` for that.
* Uncaught errors print the message without a stack trace.
* Recursion is limited to 10 000 frames (the interpreter stops at 1 024). The
  limit is high enough to stay inside the C stack and raises a catchable
  `StackOverflow` error rather than crashing.
* `os.args()` returns an empty list (the CLI does not pass script arguments yet).

## Relationship to the interpreter

The interpreter (`TERN/`) stays the reference implementation: it is what the
compiler tests, the REPL, the linter and `tern run` use. The native backend
consumes exactly the bytecode the interpreter runs, so any difference in
behaviour is a backend bug — `native/run_tests.py` exists to catch those.
