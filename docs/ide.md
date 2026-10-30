# TERN Studio

TERN Studio is a desktop IDE for TERN, modelled on JetBrains Rider: a shell
with tool windows around an editor, driven by a language server that runs as a
**separate process**. Nothing in the window does its own parsing — every
diagnostic, completion, hover and symbol you see arrived over the Language
Server Protocol.

![TERN Studio](ide/img/studio.png)

## Two halves

```
                    stdio (Content-Length framed JSON-RPC, LSP 3.17)
  ide/Studio  ──────────────────────────────────────────────►  ide/Server
  PySide6 window                                                TERN.Ide.dll
  (editor, docks, menus)                                        (.NET, owns the compiler)
```

The split matters for more than tidiness. The server links the TERN compiler
that runs the language, so the IDE reports exactly what `tools/tern run` would
report, and a client other than Studio — VS Code, Neovim, a test harness — gets
the same answers because the protocol is the only interface.

* **`ide/Server/`** — C#, `build/TERN.Ide.dll`. `Program.cs` (dispatch),
  `Rpc.cs` (framing), `Document.cs` (text, line index, positions),
  `SymbolIndex.cs` / `DeepIndex.cs` (symbols, references, structure),
  `Features.cs` (each request).
* **`ide/Studio/`** — Python, PySide6. `window.py` (the shell), `editor.py`
  (edit control, gutter, completion popup), `panels.py` (tool windows),
  `lsp.py` (the child process), `highlighter.py`, `theme.py`, `main.py`.

## Running it

```bash
python3 -m venv .venv
.venv/bin/pip install PySide6-Essentials

bash tools/ide.sh                 # open the repository
bash tools/ide.sh examples        # open a directory
bash tools/ide.sh --screenshot /tmp/studio.png   # render once, headless
```

`tools/ide.sh` finds a Python with PySide6 (`.venv`, then `/tmp/qtenv`, then
`python3`), builds `build/TERN.Ide.dll` if it is missing, and sets `DOTNET_ROOT`
so the server can start. `--theme light|darcula` and `--size 1440x900` are
passed through to the app.

There is no display in this sandbox, so Studio runs on Qt's **offscreen**
platform (`--screenshot` selects it) — it renders and behaves normally, it just
draws into a pixmap instead of a screen. Qt still wants to load four system
libraries it never calls (libGL, libEGL, libxkbcommon, libdbus); on a headless
machine `tools/ide/qt_stubs.py` writes stand-ins for them into
`build/qt-stubs/`, which is why every headless command sets
`LD_LIBRARY_PATH=build/qt-stubs`. On a desktop none of that is needed.

## What the IDE does

| Feature | Where | Notes |
| --- | --- | --- |
| Syntax highlighting | `highlighter.py` | keywords, strings, numbers, comments, types; mirrors `Lexer.cs`, including unterminated strings |
| Live diagnostics | gutter, Problems window, status bar | every keystroke, debounced; errors underline in red and fill the error strip |
| Completion | popup | after `.` and after a quote, plus Ctrl+Space; members, built-ins, imports, keywords |
| Hover documentation | tooltip | name, kind and signature, from the server's symbol index |
| Go to definition | Ctrl+B | jumps to the declaration, in another file if need be |
| Find usages | Alt+F7 | every reference, including inside strings |
| Rename | Shift+F6 | refuses duplicates, keywords and non-identifiers |
| Structure view | left dock | the file's declarations; selecting one jumps there |
| Search Everywhere | Ctrl+Shift+A | files, symbols and text in one list |
| Run | F5 | runs the current file on the interpreter and streams output to the Run window |
| Compile | Ctrl+F5 | builds and runs it through the native backend |
| Reformat | Ctrl+Alt+L | the server's formatter, 4-space indent |
| Themes | View → Theme | Darcula and light, applied to editor *and* tool windows |
| Tool windows | View | Project, Structure, Run, Problems, Terminal |
| Remembers state | settings | last project, theme, size, open files |

Shortcuts are ordinary Qt ones, so `Ctrl+Q`, `Ctrl+S`, `Ctrl+W`, `Ctrl+Tab`
and `Ctrl++`/`Ctrl+-` behave as expected too.

### Editor details

Line numbers, the current-line highlight, a bracket-matching strip, error
underlines in the gutter, a completion popup that filters as you type and
inserts with Enter or Tab, hover tooltips, and per-tab undo history. The status
bar carries the problem counts, the cursor position, the language, the encoding
and the state of the syntax server — `ready`, `starting` or `stopped`, which
is the first thing to look at when nothing responds.

## Testing

Both halves are covered, and both are part of `tools/test.sh`:

```bash
bash tools/test-ide.sh                                   # 36 assertions, protocol only
LD_LIBRARY_PATH=build/qt-stubs .venv/bin/python tools/ide/ui_test.py   # 23 checks, real window
```

`tools/test-ide.sh` drives `TERN.Ide.dll` over stdio with no UI at all: framing,
`initialize` capabilities, didOpen/didChange, diagnostics for a parse error and
their disappearance when fixed, completion items and their sorts, hover,
definition, references, rename (including the refusals), document and workspace
symbols, formatting, and clean `shutdown`/`exit`. `tools/ide/ui_test.py` builds
the real window offscreen and checks what a user would: the outline fills, a
broken line shows up in the editor *and* the Problems window, `math.` offers
`pi` and `sqrt`, hover returns docs, Run prints the program's output, the theme
switch takes effect, and a second file opens as a tab.

Screenshots in this page are generated by `tools/ide/shots.py`, not taken by
hand, so they cannot drift from the code:

```bash
LD_LIBRARY_PATH=build/qt-stubs .venv/bin/python tools/ide/shots.py
```

## Known limits

Honest list, in the order they would annoy someone:

* **No debugger.** There are no breakpoints, no stepping and no variable pane.
  Run and Compile work; debugging does not exist yet. The compiler has no
  breakpoint hook to attach to, so this is a feature to build, not a switch to
  turn on.
* **No VCS panel.** Changes are not shown in the gutter and there are no
  commit dialogs. Use a terminal.
* **No project model.** The tree is a file browser; a directory is a project.
  There are no build configurations beyond "run this file".
* **Single-file analysis by default.** Cross-file navigation follows `import`,
  but a symbol defined in a file you have never opened is only found by the
  workspace symbol index, which scans on demand.
* **The formatter is conservative** — indentation and trailing whitespace, not
  a reflow. It will not re-wrap long argument lists.
* **PySide6 is a dependency**, and a heavy one (~150 MB). The server is not: it
  is the same self-contained .NET build everything else uses.

## Doing without a screen

Everything here was developed and tested on a machine with no display, which is
worth recording because it shapes the tests:

* Qt runs on the `offscreen` platform plugin; `window.grab()` returns a real
  pixmap, so screenshots are exact.
* The four missing system libraries are replaced by stand-ins; Qt only needs
  them to *link*, never to run.
* A Qt popup is a separate top-level window and therefore absent from
  `window.grab()`, so `shots.py` draws the popup onto the frame itself at its
  mapped position. Without that the completion screenshot looks empty.
* `ui_test.py` asserts on widget state and text, never on pixels, so it passes
  the same way on a desktop with a display.
