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
| Language, compiled (`native/`) | complete, same output | `native/run_tests.py` (33), `native/parity.py` (44 programs) |
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
3. **The window layer is Windows only.** `d3d12.window` opens a real Win32
   window and `d3d12.present` puts the frame in it, through a flip-model swap
   chain where one can be made. `user32`, `gdi32`, `kernel32` and `dxgi` are
   loaded at run time, so a Linux build answers `nil` and a Linux window needs
   an X11 or Wayland surface — neither exists. The swap chain path also has no
   `ResizeBuffers`: a resized window is stretched by DXGI rather than
   re-created, and a device reset is not handled beyond falling back to the
   blit. Both are Windows-only to test and are the next two items there.
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
6. **Neither renderer has a depth buffer.** `d3d12.draw` and `gl.draw` take a
   clip-space position and a colour per vertex and nothing else, so everything
   is at the same depth and the order the triangles arrive in is the order they
   paint. A scene that is not a convex solid - or one where two visible
   triangles overlap - needs the caller to sort them, which the example does and
   which the example also gets wrong for part of a turn, which is why it culls
   back faces instead. The general answer is a depth-stencil target, a depth
   test in the pipeline state and a `z` on the vertex, and that is a change to
   the vertex format both modules share.
7. **Server-side TLS is missing.** `net.listen(port, {"tls": true})` raises
   `TlsError: server TLS is not supported`: a script can be a TLS client but not
   a TLS server. Both back ends would need the other half of the handshake
   (OpenSSL's `SSL_accept`, .NET's `SslStream` on the accepted socket).
8. **Map writes are memory bound, not compute bound.** Not a correctness
   problem; listed because it is the one benchmark where compiling buys nothing
   and it will not improve until the table's layout does (a bucket array with
   inline keys, or a chunked table that avoids the doubling rehash).
9. **Small allocation churn.** The native collector is conservative: an integer
   that happens to look like an object address can keep an object alive, and the
   heap floor is 16 MiB, so a tiny program's RSS is not tiny. The interpreter
   leaves that to .NET.
10. **`fs` error text differs slightly.** A compiled binary names the path as the
   script wrote it; the interpreter reports the resolved absolute path. Same
   error kind, different text, which `parity.py` tolerates and a user might not.
11. **Diagnostics in compiled binaries.** Compiler warnings from
   `./tools/tern check` are not emitted by binaries, and there is no sandbox:
   `--no-fs`, `--no-net`, `engine.AllowFileSystem` and `engine.AllowNetwork`
   belong to the interpreter, since a binary has no engine to configure.

Out of scope by request: DLSS, and FTP (SFTP is the supported transfer, because
SSH is what the servers actually offer).

## Recently fixed

* **The cube's faces broke up for part of the turn.** Neither renderer has a
  depth buffer - the vertex format is a clip-space position and a colour, with
  no depth anywhere - so the order the triangles are submitted in is the order
  they paint. The example sorted them far to near by average depth, which is the
  usual shortcut and is not a valid painter's order even for a cube: at some
  angles a back face's average lands nearer than a front face's, it paints over
  it, and the solid looks broken for a few degrees of the turn. It was found by
  computing the ordering over a full turn rather than by eye: 74 triangle pairs
  on 360 sampled angles are painted in the wrong order.
  `examples/rotating_cube.tern` now culls back faces - the solid is convex and
  opaque, so a face pointing away from the camera is completely hidden and the
  paint order stops mattering. The sort stays for scenes that are not convex.
  The mock can check the same thing: with `TERN_D3D12_CHECK_WINDING=1` it
  computes each triangle's projected winding and fails the run if any faces away
  from the camera, which is what `tools/test-d3d12.sh` does for the cube. The
  frames are also checked for containing at most three face colours - a cube
  shows three sides at once, so a fourth colour is a face that should be hidden.
* **The window opened, showed the clear colour, and no cube.** Direct3D 12 has
  no default viewport: until `RSSetViewports` is called the viewport list is
  empty and the rasteriser discards every triangle, while
  `ClearRenderTargetView` - which never consults it - still fills the target.
  TERN set a root signature, a pipeline state, a topology and a vertex buffer,
  and then drew, never once saying where it was allowed to draw. Both back ends
  now set a viewport covering the target and a matching scissor rect in
  `d3d12.draw`, and the layouts are checked against `d3d12.h`
  (`D3D12_VIEWPORT` 24 bytes with `MaxDepth` at 20, `D3D12_RECT` 16 bytes with
  `bottom` at 12 - the SDK's `tagRECT` spells its members in lower case).
* **Why no test noticed: the mock drew a flat colour.** `DrawInstanced` in the
  mock painted the whole target with the first vertex's colour, whatever state
  the list was in, so a renderer that rasterised nothing still produced a
  picture and still passed every comparison. The mock now interpolates the
  triangle the way the examples' pass-through shader would, into a real pixel
  buffer that the copy-out reads, and a draw with no viewport, no scissor rect
  or no vertex buffer prints `DRAW WITHOUT VIEWPORT` and fails the suite.
  Proved by removing the two calls from each back end in turn: the suite goes
  red with that exact line.
* **The native window was upside down.** The interpreter blitted bottom-row-first
  pixels and flipped them once for a top-down DIB; the native backend flipped
  again on data that was already top-first. A cube is nearly symmetric, so a
  screenshot would not have said so - the frame checksums did, the moment the
  mock started drawing something that was not a flat colour. Both back ends now
  hand the window the same layout `d3d12.pixels()` returns.
* **`!=` meant `==` in a compiled binary.** The fused compare the compiler
  emits for two locals took the equality answer for the "not equal" instruction,
  so `if (a != b)` branched as though `a == b` - and only when the operands were
  not integers, because integers take a fast path that was right. `==` was right,
  printing `a != b` was right, and the interpreter was right; the one thing that
  was wrong was the branch a compiled program actually took, which is the thing
  a test has to look at. Found by a diagnostic program that counted colours with
  a string key and compared it against a stored string, and caught by
  `tests/03_control.tern` now.
* **The window blitted instead of presenting.** `d3d12.present` read the frame
  back to system memory, converted it and handed it to `StretchDIBits`: a copy
  of the whole picture, a channel swap and a frame of latency, every frame, for
  a picture the GPU had just made. It now goes through an `IDXGISwapChain1`
  made with `IDXGIFactory2::CreateSwapChainForHwnd` (two `FLIP_DISCARD`
  buffers), copies the render target into the next back buffer on the GPU in
  the same command list, moves it to `PRESENT` and calls `Present(1, 0)`, which
  also paces the loop on the vertical blank. The blit is still there and is
  still what a machine without `IDXGIFactory2` gets — Windows 7, or a mock told
  to refuse one with `TERN_DXGI_NO_FACTORY2=1`. The mock follows the GPU copy
  into the back buffer and checksums it, and the suite compares those checksums
  against the blit's: the two ways of showing a frame have to produce the same
  pixels, or the new one is a different picture rather than the same one
  sooner. The swap chain's own state is tracked per buffer — a barrier naming a
  state the buffer is not in is what removes a device.
* **A silent test.** The PNG assertion in `tools/test-d3d12.sh` was a bare
  `python3` block: when it failed, the message appeared on the terminal and the
  script still exited 0, so the suite reported success. It is guarded now, and
  the assertion checks what a rasteriser must produce rather than one exact
  colour.
* **A saved PNG reported `0 bytes` on Windows while the file was fine.** The
  writer wrote the picture and the size was asked for from the wrong half of
  `struct stat`. mingw changes `off_t`, `st_size` and the entry point `stat()`
  together, but only if `_FILE_OFFSET_BITS` is defined *before* the first
  system header is read — set it later and the structure stays 32-bit while
  `stat()` resolves to the 64-bit function, so `st_size` is read from an offset
  the kernel never wrote (`struct stat` 48 bytes and `st_size` at 20 against
  `_stat64`'s 56 bytes and 24). The define now sits at the top of
  `native/runtime/tern_rt.h`, before every include, and
  `native/runtime/tern_platform.h` asserts the two structures agree, that
  `off_t` and `st_size` are 8 bytes, and that their offsets match — so this
  cannot come back quietly on any target. `d3d12.save` and `gl.save` now return
  the number of bytes they wrote, and `examples/d3d12_diagnose.tern` prints
  that count beside `fs.size` of the same file and fails the step if they
  disagree, which is the pair that exposed it.
* **Every clear warned that the render target had no clear value.** The
  Direct3D 12 debug layer said the resource was created without an optimized
  clear value, which means each clear is ordered against the previous contents
  instead of replacing them. `CreateCommittedResource` now receives a
  `D3D12_CLEAR_VALUE` matching the target's format and the colour the example
  clears to, in both back ends; the structure is asserted to be 20 bytes with
  its union at offset 4, checked against `d3d12.h` by
  `tools/d3d12/gen_layout_check.py`, and the mock fails the run if a render
  target arrives without one or with a mismatched format.

* **The first real GPU removed the device on the first copy: `CopyTextureRegion`
  was missing an argument.** The method is
  `CopyTextureRegion(pDst, DstX, DstY, DstZ, pSrc, pSrcBox)`. TERN calls it
  through a function pointer it declares itself, both back ends declared five
  parameters instead of six, and the driver therefore read the source location
  as `DstZ`, the next stack slot as the source location and the one after that
  as the box. That is not a compile error and it cannot be one — the call goes
  through a pointer TERN builds from the vtable slot — and the mock driver was
  compiled from the same wrong signature, so it agreed. On an RTX 4080 SUPER
  the first `d3d12.pixels` removed the device, and everything after it
  (`d3d12.save`, the pipeline) failed because the device was already gone.
  Both typedefs now carry `DstZ`, and `tools/d3d12/mock_d3d12.c` is compiled
  against the documented signature and checks that the arguments arrive where
  they belong: it prints `COPY ARGUMENT MISMATCH` and the script fails.
  Proved by putting the old five-argument typedef and call back: the mock
  rejects the copy, the pixels come back empty, and `tools/test-d3d12.sh`
  fails on the disagreement between the back ends.
* **Nothing on a real machine said why the device went.** A driver reports a
  removal at whatever call touches the device next, which is how a broken
  barrier or a bad copy surfaced as a map failure in `save_frame`. Both back
  ends now ask `GetDeviceRemovedReason` after every submit — `d3d12.clear`,
  `d3d12.draw`, and the copy behind `d3d12.pixels`, `d3d12.save` and
  `d3d12.present` — and say which call did it and what the driver called it
  (`the GPU hung and the driver reset it (TDR)`, `out of memory`, …), with the
  raw code included when the code is not one TERN knows. `d3d12.debug(true)`
  additionally turns on the Direct3D 12 debug layer before the device is
  created; the driver's own validation messages are then printed, with their
  severity, immediately after each submit, which is the difference between
  "the device was removed" and "CopyTextureRegion: resource state is invalid".
  A machine without the optional "Graphics Tools" feature answers `false` and
  says so once, in words that name the setting. All of it runs headless: the
  mock has a debug interface and an info queue of its own, and
  `tools/test-d3d12.sh` checks that the stored message reaches the program in
  both back ends with identical wording.

* **A fix that looked like it had not worked.** The report after the resource
  state fix was the same sentence as before it —
  `the readback buffer could not be mapped (0x887a0005)` at
  `save_frame (rotating_cube:360)` — with no reason attached. Only two things
  produce that wording: a build from *before* the fix, since the fixed native
  backend appends the driver's reason, or the interpreter, which never asked
  for one until it was taught to. The trace pins the rest: line 360 is
  `d3d12.save` and line 460 is `main()` in the version of the example before
  the window arrived, and both moved when it did, so the program predated that
  too. Two things made a stale build indistinguishable from a failed fix, and
  both are gone. The binding now stamps itself
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
  a real Win32 window, `d3d12.present(window, device)` puts the rendered frame
  in it through a flip-model swap chain (or a blit where there is no DXGI
  factory to make one) and pumps its messages, and
  `d3d12.closeWindow(window)` closes it.
  The example runs the window by default on Windows and falls back to PNG
  frames and text where there is no `user32`, so nothing about it needs a
  display to run - and when it does fall back on a machine that *has* Direct3D
  12, it says so in one line, because the text on its own reads like a program
  that was only ever an ASCII renderer. It is tested headless: `tools/d3d12/mock_user32.c` fakes
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
  quoting the HRESULT. The copy itself no longer parks the target in `COMMON`
  afterwards: it transitions to `COPY_SOURCE` first and stays there, so a
  second read in a row needs no barrier and the render-target layout survives
  until the next draw, which is both faster and the state the driver prefers.
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
