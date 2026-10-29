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
