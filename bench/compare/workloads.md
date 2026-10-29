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

Best of seven runs per language, measured on a 2 vCPU Xeon sandbox (2.6 GHz,
.NET 10.0.11, Lua 5.4.9, LuaJIT 2.1, Node 22). Times are in-process
milliseconds; every language prints the same result for every workload.

| Workload   | TERN | Lua 5.4 | LuaJIT | Node 22 | C# (.NET 10) | TERN / Lua |
| ---------- | ---: | ------: | -----: | ------: | -----------: | ---------: |
| `fib`      |  113 |      51 |      6 |       8 |            5 |      2.21x |
| `loop`     |  101 |      30 |     12 |       8 |            4 |      3.37x |
| `maps`     |  204 |     140 |     55 |      86 |           61 |      1.45x |
| `bintree`  |   33 |      26 |      6 |       4 |           22 |      1.25x |
| `strings`  |   16 |      10 |      5 |       2 |            4 |      1.54x |

### What is left on the table

TERN is a bytecode interpreter written in C#, so it is compared against the
interpreted Lua 5.4 (LuaJIT, Node and Roslyn-compiled C# are tracing/AOT
compilers and are 1-2 orders of magnitude ahead on numeric code):

* **Dispatch cost.** Every opcode costs roughly 8-10 cycles: jump-table
  dispatch, a bounds-checked `code[ip++]`, and the `_sp` update. Lua's `switch`
  plus register file is a few cycles cheaper per instruction.
* **Struct copies.** A `Value` holds a GC reference, so copying one into the
  stack or a local array is a `CORINFO_HELP_ASSIGN_BYREF` call rather than two
  inline moves. This is why `maps` (many stores) and `loop` (a push and a pop
  per iteration) trail Lua much more than `bintree` (few values per node).
  Splitting the stack into parallel `long[]`/`object?[]` arrays, or shrinking
  `Value` to 16 bytes so it returns in registers, are the two obvious next
  steps; both were prototyped and were a wash overall (the 16-byte variant wins
  on `maps`, loses on `bintree`).
* **No register allocation.** Operands round-trip through the value stack
  instead of registers, which the `*LocalLocal` / `*LocalConst` fused opcodes
  only partly mitigate.

### What TERN already does

* 16-byte instructions, a single dispatch loop, and jump targets patched at
  emit time.
* Fused arithmetic (`*LocalConst`, `*LocalLocal`), fused compare-and-branch, and
  fused store (`AddLocalConstStore`, `AddLocalLocalStore`, `AddLocalStack`, ...)
  opcodes, so `total += i % 7` is three instructions.
* Counted loops (`ForPrep*` / `ForNext*`): one opcode per iteration with an
  increment, a comparison and an in-place counter update.
* `x % k` with a constant divisor uses a precomputed reciprocal (`Math.BigMul`)
  with an exact range check, falling back to the hardware divide when needed.
* Frames are pooled and pre-populated, reserve their whole expression stack on
  entry, and copy arguments with an unrolled switch for the first nine arities.
* Constant folding, direct-threaded calls, and a `[MethodImpl(AggressiveOptimization)]`
  interpreter entry point so the loop is fully optimised on first call while the
  rest of the runtime still tier-quick-jits for a fast start (~86 ms).
