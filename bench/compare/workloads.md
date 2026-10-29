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

Best of nine runs per language on a 2 vCPU Xeon sandbox (2.6 GHz, .NET 10.0.11,
Lua 5.4.9, LuaJIT 2.1, Node 22). Times are in-process milliseconds; every
language prints the same result for every workload. TERN's own numbers are
stable across runs (within a few percent); the other runtimes swing by up to
30% on this shared machine, and Lua's `maps` figure in particular moves with
its garbage collector, so read the ratios as indicative rather than exact.

| Workload   | TERN | Lua 5.4 | LuaJIT | Node 22 | C# (.NET 10) | TERN / Lua |
| ---------- | ---: | ------: | -----: | ------: | -----------: | ---------: |
| `fib`      |  157 |      57 |      8 |      10 |            5 |      2.75x |
| `loop`     |  104 |      37 |     14 |      10 |            6 |      2.81x |
| `maps`     |  148 |     200 |     60 |     100 |           68 |      0.74x |
| `bintree`  |   45 |      45 |      8 |       5 |           30 |      1.00x |
| `strings`  |   18 |      14 |      6 |       3 |            4 |      1.29x |

TERN is faster than the reference interpreter on `maps` and level on `bintree`;
it is 2.8x behind on the two tight numeric loops.

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
