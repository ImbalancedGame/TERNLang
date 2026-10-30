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
Lua 5.4.9, LuaJIT 2.1, Node 22), measured 30 October 2026. Times are in-process
milliseconds; every column prints the same result for every workload, which
`tools/compare.sh` checks. `ternc` is `bench.tern` compiled to C by
`native/ternc.py` and built with `cc -O2`, so its numbers are comparable with the
others rather than including a process start.

| Workload   | TERN | ternc | Lua 5.4 | LuaJIT | Node 22 | C# (.NET 10) | TERN / Lua |
| ---------- | ---: | ----: | ------: | -----: | ------: | -----------: | ---------: |
| `fib`      |  127 |    18 |      55 |      6 |      10 |            5 |      2.30x |
| `loop`     |  116 |     5 |      31 |     12 |       9 |            5 |      3.69x |
| `maps`     |  187 |   150 |     177 |     59 |     109 |           71 |      1.06x |
| `bintree`  |   49 |     5 |      34 |      7 |       5 |           26 |      1.42x |
| `strings`  |   22 |     5 |      11 |      5 |       3 |            4 |      1.91x |

The interpreter is now slightly behind Lua on every workload — 1.06x on `maps`,
where an earlier session measured 0.74x — which is what a shared machine does to
a 30% swing. What has changed since is the `ternc` column: compiling takes 7x
off `fib`, 23x off `loop` and 10x off `bintree`, and nothing off `maps`.

The `maps` result is the interesting one. Writing 200 000 integer keys costs
about 250 ns each either way, and a scaling run (100 k / 200 k / 400 k / 800 k)
gives 25 / 51 / 101 / 199 ms compiled against 40 / 45 / 157 / 197 ms interpreted:
the two cross over as the table outgrows cache. Hashing is not the cost —
splitmix64 is a handful of multiplies — and neither is the collector
(`TERN_GC_STATS=1` reports zero collections for the whole run, because map
storage is `malloc`ed outside the GC heap). It is the random probe into a
multi-megabyte table, which is why a JIT pays the same bill.

### Per-workload notes

* `maps` - string keys are hashed once per operation without allocating a
  temporary, and `m["key${i}"]` compiles to a store with no receiver or index
  spill. This is the workload where the C# host's optimised BCL (hashing,
  string building) does the heavy lifting.
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
