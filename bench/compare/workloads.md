# Comparative workloads

Five workloads implemented identically in TERN, Lua 5.4, LuaJIT, Node.js and C#.
Each program prints `name <result> <milliseconds>` where the millisecond figure
is measured in-process with the language's monotonic clock, so process startup
and JIT warm-up of the host are excluded.

| Workload | Definition |
| -------- | ---------- |
| `fib` | `fib(30)` with naive double recursion |
| `loop` | 5,000,000 iterations of `total += i % 7` |
| `maps` | 200,000 string-keyed inserts, then 200,000 lookups summed |
| `bintree` | build a depth-16 node tree, then count its nodes |
| `strings` | 20,000 interpolations, join, split and uppercase |

Run all of them with `tools/compare.sh`.
## Results

Best of five runs per language on a 2 vCPU Xeon sandbox (2.6 GHz, .NET 10.0.11,
Lua 5.4.9, LuaJIT 2.1, Node 22), measured 3 November 2026. Times are in-process
milliseconds; every column prints the same result for every workload, which
`tools/compare.sh` checks. `ternc` is `bench.tern` compiled to C by
`native/ternc.py` and built with `cc -O2`, so its numbers are comparable with the
others rather than including a process start.

| Workload   | TERN | ternc | Lua 5.4 | LuaJIT | Node 22 | C# (.NET 10) | TERN / Lua |
| ---------- | ---: | ----: | ------: | -----: | ------: | -----------: | ---------: |
| `fib`      |  140 |    18 |      57 |      6 |       9 |            5 |      2.44x |
| `loop`     |  126 |     5 |      32 |     12 |       8 |            5 |      3.95x |
| `maps`     |  102 |    98 |     158 |     57 |      98 |           69 |      0.64x |
| `bintree`  |   43 |     5 |      29 |      7 |       5 |           26 |      1.48x |
| `strings`  |   16 |     3 |      11 |      5 |       3 |            4 |      1.41x |

The interpreter is behind Lua on the compute-bound workloads — 2.4x on `fib`,
4.0x on the numeric loop — and ahead of it on `maps`, at 0.64x.

The `maps` result is the interesting one, because it used to be TERN's worst
column (187 interpreted, 150 compiled) and the table was never the reason. Two
separate costs were being charged to it:

* **`ternc` formatted numbers with `snprintf`.** `"key${i}"` in the generated C
  reached `snprintf("%lld")` twice per value — once to measure the digits and
  once to write them — and `snprintf` is one of the slowest functions in libc
  (locale, format parsing, a `FILE`-shaped code path). A hand-written decimal
  loop took the interpolation from 35 ms per 200 000 values to 18, and the whole
  workload from 141 ms to 94 in a same-machine A/B of the 5-run suite. The same
  function formats `str(n)`, `println`, JSON and map keys, so `strings` moved
  with it (5 → 3 ms).
* **The interpreter ran the map's helper methods as tier-0 JIT code.** `maps` is
  the third workload in the suite and lasts 200 ms; the methods it spends that
  time in — `TernMap.TryGet`, `TernMap.Set`, `Value.GetHashCode`,
  `StringHash`, `LoadIndexed`, `StoreIndexed`, `ConcatValues` — were still
  running the fast-to-start, slow-to-run compilation .NET uses for the first
  seconds of a process, and were promoted just after the measurement ended. The
  same workload run three times in one process gives 200 / 200 / 73 ms. Marking
  those methods `AggressiveOptimization` compiles them fully at their first call
  instead, which took `maps` from 195 ms to 99-104 across two 5-run sweeps and
  moved nothing else (all other workloads within noise).

What is left is genuinely the table. Writing and reading 200 000 integer keys
costs ~200 ns an operation, and a scaling run (100 k / 200 k / 400 k / 800 k)
gives 26 / 56 / 112 / 264 ms compiled against 36 / 74 / 145 / 334 ms
interpreted: the cost per operation grows with the table, so it is the random
probe into a multi-megabyte working set, not hashing (splitmix64 is a handful of
multiplies) and not the collector (`TERN_GC_STATS=1` reports zero collections —
map storage is `malloc`ed outside the GC heap).

### Per-workload notes

* `maps` - string keys are hashed once per operation without allocating a
  temporary, and `m["key${i}"]` compiles to a store with no receiver or index
  spill. Both back ends spend most of the workload building the 200 000 key
  strings, which is why the number formatting is hand-written in C and why the
  map's helpers are compiled fully on their first call.
* `bintree` - allocation dominated. Each node is one struct instance plus a
  `Value[]`; the interpreter only executes a handful of opcodes per node, so
  this is the workload closest to "how fast can a C# object graph be built".
* `fib` / `loop` - pure interpreter throughput, where the remaining gap is the
  per-opcode dispatch cost (see below).

### What is left on the table

* **Dispatch cost.** TERN's dispatch is a jump table over a `switch`; measured
  cost is ~15 cycles per opcode on this machine against ~5 for Lua's `switch`.
  The difference is not dispatch itself (a 145-case synthetic `switch` runs at
  1.5 ns/op) but the handler bodies: bounds-checked array accesses, `Value`
  copies, and the stack pointer updates.
* **Struct copies.** A `Value` holds a GC reference, so copying one into the
  stack or a local array is a `CORINFO_HELP_ASSIGN_BYREF` call rather than two
  inline moves. Splitting the stack into parallel `long[]`/`object?[]` arrays is
  the obvious next step; a 16-byte `Value` was prototyped and was a wash overall
  (it wins on `maps`, loses on `bintree`).
* **No register allocation.** Operands round-trip through the value stack
  instead of registers, which the fused `*LocalLocal` / `*LocalConst` opcodes
  only partly mitigate.
* **Native method calls** still cost ~190 ns per call (method resolution,
  argument marshalling and the `Value` return), against ~10 ns for an
  interpreter-level function call into an already-compiled body.
