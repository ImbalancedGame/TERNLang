# Where TERN stands

A single page for "is this done, and what is left". The reference is
[`msdn/`](msdn/README.md), the language guide is [`language.md`](language.md),
and the flat generated index is [`stdlib.md`](stdlib.md).

Measured 30 October 2026 on a 2 vCPU / 2.6 GHz Linux sandbox with .NET 10,
Zig 0.17 (for the Windows target), Lua 5.4.9, LuaJIT 2.1 and Node 22. There is
no Windows machine and no GPU in the sandbox, which is the origin of most of the
caveats below.

## What works

| Area | State | Verified by |
| --- | --- | --- |
| Language, interpreter | complete | 36 golden tests, `tools/test.sh` |
| Language, compiled (`native/`) | complete, same output | `native/run_tests.py` (32), `native/parity.py` (42 programs) |
| `math` `json` `os` `fs` `text` `lists` `time` `random` | complete, both back ends | golden tests, parity |
| `net` (TCP, framing, tokens, frame ceiling, UDP) | complete, both back ends | `tests/26_net.tern`, `tools/test-tls.sh` |
| `http` / `https` | complete, both back ends | `tests/28_http.tern`, `tools/test-tls.sh` |
| `rcon` | complete, both back ends | `tests/29_rcon.tern` |
| `sftp` (OpenSSH client) | complete, both back ends | `tests/30_sftp.tern` |
| `fs` streaming (`open`/`readChunk`/`readAt`/`seek`) | complete, both back ends | `tests/31_fs_stream.tern` |
| `gl` (libEGL, OpenGL 3.3 core) | complete against a mock driver | `tools/test-gl.sh` |
| `d3d12` (device, target, HLSL, PSO, draw) | complete against a mock driver | `tools/test-d3d12.sh` |
| Windows target (`--target windows`) | builds and links, no runtime to install | `tools/test-windows.sh` (8 binaries) |
| MSDN-style reference | 278 pages, generated | `tools/gen-reference.py --check` |
| Language server (`ide/Server`) | complete, LSP 3.17 | `tools/test-ide.sh` (36) |
| TERN Studio, the desktop IDE | complete for editing, running, navigating, searching, committing, debugging | `tools/ide/ui_test.py` (65) |
| Debugger (`ide/Debug`, `TERN.Debug.dll`) | breakpoints, stepping, stack, locals, evaluate | `tools/debug_test.py` (35) |

Every suite runs before a commit, and the working tree is expected to be green
in all of them:

```
./tools/test.sh                                       39 passed
python3 native/run_tests.py                           32 passed
python3 native/parity.py                              42 compared, 0 differing
bash tools/test-gl.sh                                 ok
bash tools/test-tls.sh                                ok
bash tools/test-windows.sh                            ok
bash tools/test-d3d12.sh                              ok
bash tools/test-ide.sh                                36 passed
python3 tools/debug_test.py                           35 passed
python3 tools/gen-reference.py --check                278 pages up to date
```

## Benchmarks

Five workloads, identical in every language, timed in process by each language's
own clock. Full notes in [`../bench/compare/workloads.md`](../bench/compare/workloads.md).

| Workload | TERN | TERN compiled | Lua 5.4 | LuaJIT | Node 22 | C# |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `fib` | 127 | 18 | 55 | 6 | 10 | 5 |
| `loop` | 116 | 5 | 31 | 12 | 9 | 5 |
| `maps` | 187 | 150 | 177 | 59 | 109 | 71 |
| `bintree` | 49 | 5 | 34 | 7 | 5 | 26 |
| `strings` | 22 | 5 | 11 | 5 | 3 | 4 |

Milliseconds, best of five. Compiling is worth 7-23x on the compute-bound
workloads and nothing on `maps`, which is memory-latency bound: the same table
of 800 000 integer keys takes 199 ms compiled and 197 ms interpreted, with the
collector idle. Build cost is ~4 s and ~220 KB per binary; a compiled binary
starts in 2 ms against 112 ms for the interpreter.

### What has not been benchmarked

* **Network throughput.** No test measures requests per second through
  `http.get`, or how the framing in `net.send` compares with writing raw bytes.
  Both back ends are synchronous and single-threaded by design, so the ceiling is
  one connection at a time; the numbers would say whether the C# socket path or
  the C one is closer to it.
* **TLS cost.** `tools/test-tls.sh` proves the handshake and the certificate
  path, not what a handshake adds to a request, or how much a keep-alive
  connection saves.
* **File streaming.** `fs.readChunk` is checked for correctness, not for how it
  compares with `fs.read` on a file large enough to page.
* **The renderers.** No GPU here, so `gl` and `d3d12` have no timings at all:
  draw calls, readback and PNG encoding are unmeasured on real hardware.
* **Start-up.** The 2 ms against 112 ms figure above is a hello world; how much
  of that 112 ms is JIT, and how it scales with script size, is not measured.

## The IDE

`docs/ide.md` has the detail; the short version is that the analysis lives in a
separate process and the window is a client of it, so the IDE reports exactly
what `tools/tern run` reports.

* **Language server** (`ide/Server`, 384 KB) — LSP 3.17 over stdio with
  Content-Length framing: `initialize`, full-text sync, diagnostics,
  completion (members, built-ins, imports, keywords; `.` and `"` triggers),
  hover, definition, references, document/workspace symbols, formatting,
  rename with `prepareRename`.
* **Studio** (`ide/Studio`, PySide6) — Rider-shaped shell: menu bar, toolbar,
  Project and Structure docks, Run/Problems/Terminal tabified at the bottom,
  breadcrumbs, status bar, Darcula and light themes, remembered state.
* **Version control** (`ide/Studio/vcs.py`) — git status, staging, commit and a
  coloured diff view, all through the `git` CLI; degrades to "not a git
  repository" when there is none.
* **Debugger** (`ide/Debug`, `TERN.Debug.dll`) — breakpoints, step over/in/out,
  call stack, locals and an expression evaluator, over a line-oriented protocol
  of its own. Breakpoints are patched instructions (`Op.Break`), not a
  per-instruction check: the hook costs 3-15% when polled that way, measured,
  and nothing this way. Interpreter only; the compiled backend has no VM to
  hook.
* **Not there**: a project model beyond "a directory", multi-file *background*
  analysis, conditional breakpoints and watch expressions. None of these are
  blocked by the sandbox; they are simply not written.

## What still needs fixing

Ordered by what would bite a user first.

1. **Neither renderer has run on real hardware.** `gl` and `d3d12` are tested
   against mocks that answer the calls in the right order and paint what they
   are told to paint. That proves the plumbing — that the vertex buffer reaches
   the draw, that the readback pitch is the device's — and not that a driver
   accepts the description. Running `tools/tern tools/d3d12/probe.tern` on a
   Windows machine, and `tools/gl/probe.tern` on a machine with a GPU, is the
   check that remains. The structure mirror is already checked against the real
   `d3d12.h` by the Windows build (`tools/d3d12/gen_layout_check.py`).
2. **Windows builds have no networking or OpenGL.** `net`, `http`, `rcon`,
   `sftp` and `gl` answer `UnsupportedError` in a Windows binary; the language,
   `math`, `time`, `json`, `os`, `fs`, `text`, `lists` and `d3d12` are there.
   Winsock is the missing translation for the first four, and `wgl` or an EGL
   build for the last. The interpreter on Windows has all of them.
3. **`d3d12` draws with one vertex buffer per call.** Correct, and the wrong
   shape for a renderer that draws a thousand times a frame: each `d3d12.draw`
   creates an upload-heap buffer and waits for the GPU. A ring of persistently
   mapped buffers is the next step, and it needs the fence work that goes with
   it.
4. **Server-side TLS is missing.** `net.listen(port, {"tls": true})` raises
   `TlsError: server TLS is not supported`: a script can be a TLS client but not
   a TLS server. Both back ends would need the other half of the handshake
   (OpenSSL's `SSL_accept`, .NET's `SslStream` on the accepted socket).
5. **Map writes are memory bound, not compute bound.** Not a correctness
   problem; listed because it is the one benchmark where compiling buys nothing
   and it will not improve until the table's layout does (a bucket array with
   inline keys, or a chunked table that avoids the doubling rehash).
6. **Small allocation churn.** The native collector is conservative: an integer
   that happens to look like an object address can keep an object alive, and the
   heap floor is 16 MiB, so a tiny program's RSS is not tiny. The interpreter
   leaves that to .NET.
7. **`fs` error text differs slightly.** A compiled binary names the path as the
   script wrote it; the interpreter reports the resolved absolute path. Same
   error kind, different text, which `parity.py` tolerates and a user might not.
8. **Diagnostics in compiled binaries.** Compiler warnings from
   `./tools/tern check` are not emitted by binaries, and there is no sandbox:
   `--no-fs`, `--no-net`, `engine.AllowFileSystem` and `engine.AllowNetwork`
   belong to the interpreter, since a binary has no engine to configure.

Out of scope by request: DLSS, and FTP (SFTP is the supported transfer, because
SSH is what the servers actually offer).

## Recently fixed

* A refused connection with a timeout used to abort the interpreter: the
  connect path waited on a `Task`, whose `AggregateException` is not a
  `TernError` and so escaped `try`/`catch` and killed the process with exit 134.
  It is an ordinary `NetError` now, and `tests/26_net.tern` pins it.
* Slicing, indexing, `chars()`, iteration and `reversed()` on a character above
  U+FFFF used to cut the surrogate pair in the interpreter, which produced
  U+FFFD and lost the character. Both back ends now resolve such an index to
  the start of the character; `tests/34_unicode.tern` pins it.
* The graphics pipeline state description omitted `InputLayout` and
  `IBStripCutValue`, which made the structure 8 bytes short of the 656 the SDK
  expects. The Windows build now asserts every offset against the real header
  and fails to compile if one drifts.
