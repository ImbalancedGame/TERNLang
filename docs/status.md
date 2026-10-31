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
| Language, interpreter | complete | 35 golden runs, `tools/test.sh` |
| Language, compiled (`native/`) | complete, same output | `native/run_tests.py` (33), `native/parity.py` (43 programs) |
| `math` `json` `os` `fs` `text` `lists` `time` `random` | complete, both back ends | golden tests, parity |
| `net` (TCP, framing, tokens, frame ceiling, UDP, raw mode) | complete, both back ends | `tests/26_net.tern`, `tests/35_raw_bytes.tern`, `tools/test-tls.sh` |
| `http` / `https` | complete, both back ends | `tests/28_http.tern`, `tools/test-tls.sh` |
| `rcon` | complete, both back ends | `tests/29_rcon.tern` |
| `sftp` (OpenSSH client) | complete, both back ends | `tests/30_sftp.tern` |
| `fs` streaming (`open`/`readChunk`/`readAt`/`seek`) | complete, both back ends | `tests/31_fs_stream.tern` |
| `gl` (libEGL, OpenGL 3.3 core) | complete against a mock driver | `tools/test-gl.sh` |
| `d3d12` (device, target, HLSL, PSO, draw, window) | complete against a mock driver | `tools/test-d3d12.sh` |
| Windows target (`--target windows`) | builds and links, no runtime to install | `tools/test-windows.sh` (8 binaries) |
| MSDN-style reference | 284 pages, generated | `tools/gen-reference.py --check` |
| Language server (`ide/Server`) | complete, LSP 3.17 | `tools/test-ide.sh` (39) |
| TERN Studio, the desktop IDE | complete for editing, running, navigating, searching, committing, terminal, debugging; five themes | `tools/ide/ui_test.py` (133) |
| Packaging (`tools/package.py`) | builds the PyInstaller command and prunes Qt; the entry point runs | `tools/test_package.py` (12) |
| Starting the CLI (`tools/runner.py`) | assembly, script or `TERN_CLI`; the choice is an argv prefix, so no shell is assumed | `tools/test_runner.py` (15) |
| Debugger (`ide/Debug`, `TERN.Debug.dll`) | breakpoints (with conditions), watches, stepping, stack, locals, evaluate | `tools/debug_test.py` (41) |
| Examples (`examples/`) | HTTP server, RCON client, SFTP deploy, rotating cube, Direct3D 12 diagnostic — each run, not just compiled | `tools/test_examples.py` (65) |

Every suite runs before a commit, and the working tree is expected to be green
in all of them:

```
./tools/test.sh                                       44 passed
python3 tools/test_runner.py                          21 passed
python3 tools/test_package.py                         15 passed
python3 native/run_tests.py                           33 passed
python3 native/parity.py                              44 compared, 0 differing
python3 tools/test_examples.py                        65 passed
python3 tools/test_namespaces.py                      23 passed
bash tools/test-gl.sh                                 ok
bash tools/test-tls.sh                                ok
bash tools/test-windows.sh                            ok
bash tools/test-d3d12.sh                              ok
bash tools/test-ide.sh                                39 passed
python3 tools/debug_test.py                           41 passed
python3 tools/gen-reference.py --check                284 pages up to date
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

* **Language server** (`ide/Server`, 390 KB) — LSP 3.17 over stdio with
  Content-Length framing: `initialize`, full-text sync, diagnostics,
  completion (members, built-ins, imports, keywords; `.` and `"` triggers),
  hover, definition, references, document/workspace symbols, formatting,
  signature help, rename with `prepareRename`.
* **Studio** (`ide/Studio`, PySide6) — Rider-shaped shell: menu bar, toolbar,
  Project and Structure docks, Run/Problems/Terminal tabified at the bottom,
  breadcrumbs, status bar, Darcula and light themes, remembered state. The
  Terminal window is a shell (colour, history, Ctrl+C), not a log view.
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
  analysis, calls or arithmetic inside a breakpoint condition, and editing
  values while paused. None of these are blocked by the sandbox; they are
  simply not written.

## What still needs fixing

Ordered by what would bite a user first.

1. **The native backend has not been built on a Windows machine.** The compiler
   search now covers an MSYS2 install that is not on PATH, which was the first
   real answer to "I installed gcc and it still says none found", but the
   Windows discovery itself is only simulated here — `tools/test_runner.py`
   points `MSYS2_ROOT` at a fake tree. The pieces that made it impossible on
   that platform are fixed and tested as far as Linux allows —
   the CLI is started as `dotnet build/TERN.Runner.dll` instead of the bash
   `tools/tern` that raised `WinError 193`, the C compiler is discovered rather
   than assumed (`zig cc` when nothing else is present), and `-o app` gains
   `.exe` — but `tools/test_runner.py` proves the resolution, not a Windows
   process launch. `pip install ziglang` and one `ternc.py` run is the check
   that remains.
2. **Neither renderer has rendered a frame on real hardware.** Both are tested
   against mocks that answer the calls in the right order and paint what they
   are told to paint, which proves the plumbing — that the vertex buffer
   reaches the draw, that the readback pitch is the device's — and not that a
   driver accepts the description. For Direct3D 12 that gap is not theoretical:
   it hid a device id with the right first word and an invented tail, and a
   `Create*` call given the wrong interface id, for as long as the binding has
   existed. The ids are right now and the mock enforces them, but the remaining
   claims — descriptor heap offsets, the root signature blob, the PSO
   description, the readback pitch — have still only met a fake. Running
   `examples/rotating_cube.tern` on the Windows machine is the cheap test;
   `tools/tern tools/d3d12/probe.tern` and `tools/gl/probe.tern` on a machine
   with a GPU are the thorough ones. The structure mirror is already checked
   against the real `d3d12.h` by the Windows build
   (`tools/d3d12/gen_layout_check.py`). When a call fails there,
   `examples/d3d12_diagnose.tern` is the one to run: it makes each call in turn
   and prints what each did, so the failure names itself, and it reports the
   binding revision that produced it.
3. **The window blits, it does not present.** `d3d12.window` opens a real
   Win32 window and `d3d12.present` puts the frame in it, so
   `examples/rotating_cube.tern` shows a turning cube rather than describing
   one. But each frame is read back from the GPU and copied with
   `StretchDIBits`, which is a frame of latency and a bandwidth cost that a
   DXGI swap chain does not have. A swap chain is the next step: `Present` on
   an `IDXGISwapChain3` fed by the same render target, which also means
   handling the present-state transitions that go with it. The window layer is
   also Windows only — `user32`, `gdi32` and `kernel32` are loaded at run time
   from a Linux build and answer `nil` there, so a Linux window needs an X11 or
   Wayland surface and the same blit, and neither exists.
4. **Windows builds have no networking or OpenGL.** `net`, `http`, `rcon`,
   `sftp` and `gl` answer `UnsupportedError` in a Windows binary; the language,
   `math`, `time`, `json`, `os`, `fs`, `text`, `lists` and `d3d12` are there.
   Winsock is the missing translation for the first four, and `wgl` or an EGL
   build for the last. The interpreter on Windows has all of them. Which module
   lands in which group is no longer left to reading the preprocessor:
   `tools/test_namespaces.py` checks it, and it is how the `d3d12` gap below was
   found.
5. **`d3d12` draws with one vertex buffer per call.** Correct, and the wrong
   shape for a renderer that draws a thousand times a frame: each `d3d12.draw`
   creates an upload-heap buffer and waits for the GPU. A ring of persistently
   mapped buffers is the next step, and it needs the fence work that goes with
   it.
6. **Server-side TLS is missing.** `net.listen(port, {"tls": true})` raises
   `TlsError: server TLS is not supported`: a script can be a TLS client but not
   a TLS server. Both back ends would need the other half of the handshake
   (OpenSSL's `SSL_accept`, .NET's `SslStream` on the accepted socket).
7. **Map writes are memory bound, not compute bound.** Not a correctness
   problem; listed because it is the one benchmark where compiling buys nothing
   and it will not improve until the table's layout does (a bucket array with
   inline keys, or a chunked table that avoids the doubling rehash).
8. **Small allocation churn.** The native collector is conservative: an integer
   that happens to look like an object address can keep an object alive, and the
   heap floor is 16 MiB, so a tiny program's RSS is not tiny. The interpreter
   leaves that to .NET.
9. **`fs` error text differs slightly.** A compiled binary names the path as the
   script wrote it; the interpreter reports the resolved absolute path. Same
   error kind, different text, which `parity.py` tolerates and a user might not.
10. **Diagnostics in compiled binaries.** Compiler warnings from
   `./tools/tern check` are not emitted by binaries, and there is no sandbox:
   `--no-fs`, `--no-net`, `engine.AllowFileSystem` and `engine.AllowNetwork`
   belong to the interpreter, since a binary has no engine to configure.

Out of scope by request: DLSS, and FTP (SFTP is the supported transfer, because
SSH is what the servers actually offer).

## Recently fixed

* **A fix that looked like it had not worked.** The report after the resource
  state fix was the same sentence as before it —
  `the readback buffer could not be mapped (0x887a0005)` at
  `save_frame (rotating_cube:360)` — because it was the same binary: line 360
  is `d3d12.save` and line 460 is `main()` in the tree *before* the fix, and
  neither moved when it landed. Two things made that indistinguishable from a
  failed fix, and both are gone. The binding now stamps itself
  (`d3d12.info()["build"]`, printed by the cube example and by
  `examples/d3d12_diagnose.tern`), so a report says which build produced it.
  And a failure now names the call that broke the device rather than the call
  that noticed: after every submit both back ends ask
  `GetDeviceRemovedReason`, so a barrier that removes the device during
  `d3d12.clear` says so there instead of surfacing as a map failure three calls
  later. The interpreter's messages were also missing the driver's reason
  entirely — `0x887A0005` there said only what the code said, while the native
  backend had been taught to ask; both now report the same sentence, down to
  the case of the hex. A mock knob removes the device mid-run
  (`TERN_D3D12_REMOVED_AFTER`) or fails the readback map (`TERN_D3D12_MAP_REMOVED`),
  and `tools/test-d3d12.sh` compares the two back ends' exact wording for both.

* **`examples/rotating_cube.tern` was ASCII art, not a rendering.** That was a
  fair description: the only thing it could do with a GPU was write PNGs into a
  folder, and the thing on screen was a text rasteriser. There was no window
  layer at all — no `user32`, no presentation surface, no message loop — so a
  cube that turned in a window could not be written, only described as the next
  step. Both back ends have one now: `d3d12.window(width, height, title)` opens
  a real Win32 window, `d3d12.present(window, device)` blits the rendered frame
  into it and pumps its messages, and `d3d12.closeWindow(window)` closes it.
  The example runs the window by default on Windows and falls back to PNG
  frames and text where there is no `user32`, so nothing about it needs a
  display to run. It is tested headless: `tools/d3d12/mock_user32.c` fakes
  `user32`, `gdi32` and `kernel32`, and `tools/test-d3d12.sh` opens a window,
  presents frames in both back ends, compares the checksums of the pixels that
  reached the window, and checks that the window closes itself the way a person
  closing it would. The structures the window uses (`WNDCLASSEXW`, `MSG`,
  `BITMAPINFOHEADER`, `BITMAPINFO`, `RECT`) are checked against the real
  `windows.h` by the Windows build (`tools/d3d12/gen_window_layout_check.py`).
* **Every Direct3D 12 barrier named the wrong "before" state.** `clear` moved
  the target to `COPY_SOURCE` for the readback and left it there; `draw` then
  told the driver the resource was in `COMMON` and moved it to
  `RENDER_TARGET`. The driver had never been told about `RENDER_TARGET` →
  `COPY_SOURCE`, so from the second frame of any clear-and-draw program every
  barrier was a lie about what it was about to change. Windows does not
  diagnose that: it is undefined behaviour, and the device was sometimes
  removed instead, which surfaced as `the readback buffer could not be mapped
  (0x887A0005)` in `save_frame`, several calls away from the bug. Both back
  ends now track the real state (`TernD3D12Target.state`, `Target._state`) and
  transition from it, skipping no-op transitions, and the mock driver tracks
  the state too: it prints `RESOURCE STATE MISMATCH: a barrier claims state 0
  but the resource is in 4` and `tools/test-d3d12.sh` fails on it. Proved by
  reinstating the old pair and watching the script fail. A map failure is also
  followed by `GetDeviceRemovedReason` now, so the next report of this kind
  says whether the device was removed, reset, or out of memory instead of only
  quoting the HRESULT.
* **The Direct3D 12 binding would have failed on any real GPU.** Every `Create*`
  was given `IID_ID3D12Device` instead of the id of the interface it was
  creating — `CreateCommandQueue` wants `IID_ID3D12CommandQueue`, a resource
  wants `IID_ID3D12Resource`, and Windows answers `E_NOINTERFACE` for anything
  else. The device id itself was also wrong: `189819f1-1b6b-4ec5-baaf-…`
  instead of `189819f1-1db6-4b57-be54-…`, right first word and an invented
  tail. The mock driver ignored the id entirely, so every test passed; the
  first real call returned `0x80004002`. All the ids are now the `uuid()`
  values from `d3d12.idl`/`dxgi.idl`, both back ends ask for the right
  interface, and the mock rejects a mismatch the way Windows does — proved by
  putting the old id back and watching `tools/test-d3d12.sh` fail with the
  user's exact error. An unknown HRESULT is also translated now: the message a
  person saw said "Direct3D 12 is not available on this system", which pointed
  at their machine rather than at the bug.
* A Windows binary had no `d3d12` namespace at all: the registration sat inside
  the `#else` of the platform split, which is the non-Windows half, so every
  call answered `TypeError: map has no method 'available'` — including in
  `examples/rotating_cube.tern`, which the report came from. It is registered
  outside the split now, where the header already worked on both systems.
  Nothing on Linux could see this: the Linux branch was correct and
  `tools/test-windows.sh` only proves a Windows binary builds. `net.receiveSome`
  had the same shape of bug in the other direction — present in the real
  namespace, missing from the Windows stub list, so a Windows call to it failed
  with the same `map has no method` message instead of the module's own
  `UnsupportedError`. `tools/test_namespaces.py` now compares the two halves of
  `namespace_map` with each other and with `tern docs`, and fails on either bug
  (`43 passed` → `44 passed`, one new group).
* `examples/rotating_cube.tern` guessed that `gl.available()` would answer even
  when `gl` was not built for the platform. On Windows it raises, one line after
  the same class of failure had already been reported from `d3d12`, so the
  fallback path is inside the `try` now: an unusable backend costs a sentence,
  never the run.

* The runner used to treat any argument beginning with `--` as its own, so
  `tern run deploy.tern --dry-run` performed a real deployment and the script
  never saw its flag. Options are now recognised only before the script path;
  everything after it is `os.args()`.
* Relative paths in `fs`, `gl` and `d3d12` resolved against the script's
  directory in the interpreter but against the working directory in a compiled
  binary, so a passing script could fail once run from elsewhere. Both back ends
  use the working directory now; only `import` still resolves against the
  importing file.
* `while true` never ran its body: the constant-condition path patched the body's
  exit jump to the loop exit. It loops, and `tests/13_loops.tern` covers it.
* `text.toBytes` and `text.fromBytes` were added inside the `#ifndef _WIN32`
  block of the native runtime, so a Windows binary would not compile at all
  while every Linux suite stayed green. They are in the cross-platform section
  now, `35_raw_bytes` is part of the Windows cross-build, and `ternc.py` finds a
  `ziglang` installed into the checkout's virtualenv — the reason the gap only
  showed up when the suite was run by hand with `TERN_ZIG` set.
* `text.fromBytes` reported a missing list with a different sentence in each
  back end; the native runtime now matches the interpreter word for word, which
  `tests/35_raw_bytes.tern` pins.

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
