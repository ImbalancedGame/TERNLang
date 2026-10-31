#!/usr/bin/env python3
"""Headless UI test for TERN Studio.

Builds the real window with Qt's offscreen platform, drives it the way a user
would — open a file, type, ask for completion, run the program, switch theme —
and asserts on what the widgets show. The window has no display here, but
everything else is the application: the same widgets, the same syntax server
subprocess, the same code paths.

    python3 tools/ide/ui_test.py            run the suite
    python3 tools/ide/ui_test.py -v         show each check
    python3 tools/ide/ui_test.py --shots    also write screenshots to build/ide
"""

import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
sys.path.insert(0, ROOT)
from ide.Studio.runtime import environment as _runtime_environment  # noqa: E402

for _name, _value in _runtime_environment(ROOT).items():
    os.environ.setdefault(_name, _value)


def _luminance(hex_colour):
    """Relative brightness of "#rrggbb", for "is this theme actually darker"."""
    value = hex_colour.lstrip("#")
    red, green, blue = (int(value[i:i + 2], 16) for i in (0, 2, 4))
    return 0.2126 * red + 0.7152 * green + 0.0722 * blue


class Checks:
    def __init__(self, verbose):
        self.verbose = verbose
        self.passed = 0
        self.failed = []
        self.problems = []

    def check(self, name, condition, detail=""):
        if condition:
            self.passed += 1
            if self.verbose:
                print(f"  ok   {name}")
        else:
            self.failed.append(name)
            print(f"  FAIL {name}" + (f" — {detail}" if detail else ""))

    def warn(self, message):
        self.problems.append(message)
        print(f"  note {message}")


def pump(app, milliseconds):
    """Run the event loop for a while so responses and processes make progress."""
    from PySide6.QtCore import QEventLoop, QTimer
    loop = QEventLoop()
    QTimer.singleShot(milliseconds, loop.quit)
    loop.exec()
    app.processEvents()


def main():
    verbose = "-v" in sys.argv or "--verbose" in sys.argv
    shots = "--shots" in sys.argv

    from PySide6.QtCore import QProcess, Qt
    from PySide6.QtTest import QTest
    from PySide6.QtWidgets import QApplication
    from PySide6.QtGui import QTextCursor
    from PySide6.QtCore import Qt

    app = QApplication(["tern-studio-test"])
    sys.path.insert(0, ROOT)

    from ide.Studio.theme import THEMES, apply_to, stylesheet
    from ide.Studio.window import StudioWindow

    apply_to(app)
    app.setStyleSheet(stylesheet(THEMES["darcula"]))

    checks = Checks(verbose)
    print("TERN Studio: UI suite")

    window = StudioWindow(ROOT, theme="darcula")
    window.resize(1440, 900)
    window.show()
    pump(app, 200)

    # ---------------------------------------------------------- startup
    checks.check("the window opens with the example file", len(window.tabs) >= 1, str(list(window.tabs)))
    checks.check("the syntax server started", "ready" in window.status_server.text().lower(),
                 window.status_server.text())
    checks.check("the project tree shows the repository", window.project_panel.tree.model() is not None)

    # ----------------------------------------------------------- the runner
    checks.check("the window knows how it runs a program",
                 isinstance(window.launcher, list) and window.launcher,
                 str(window.launcher))
    checks.check("the Run window says which command it will use",
                 any("Runner:" in line for line in window.console.output.toPlainText().splitlines()),
                 window.console.output.toPlainText()[:200])
    checks.check("the chosen runner can actually start",
                 window.runtime_missing is None or "Runner:" in
                 window.console.output.toPlainText(),
                 str(window.runtime_missing))
    checks.check("the title bar names the IDE", window.windowTitle() == "TERN Studio")

    # ---------------------------------------------------------- analysis
    pump(app, 800)
    checks.check("the outline is populated from the server",
                 window.structure_panel.tree.topLevelItemCount() >= 2,
                 str(window.structure_panel.tree.topLevelItemCount()))

    outline_names = [window.structure_panel.tree.topLevelItem(i).text(0).strip()
                     for i in range(window.structure_panel.tree.topLevelItemCount())]
    checks.check("the outline lists the struct", any("Rule" in name for name in outline_names),
                 str(outline_names))
    checks.check("the outline lists the function", any("classify" in name for name in outline_names),
                 str(outline_names))

    # ------------------------------------------------------- diagnostics
    area = window._area()
    editor = area.editor
    original = editor.toPlainText()

    # Introduce a syntax error and let the server see it.
    broken = original.replace("let log = [", "let log = [;;;")
    editor.setPlainText(broken)
    window.server.did_change(window.server and _uri(editor.path), broken)
    pump(app, 900)

    checks.check("the broken file produces problems", len(editor.problems) >= 1,
                 str(editor.problems[:2]))
    checks.check("the problems window lists them",
                 window.problems_panel.tree.topLevelItemCount() >= 1,
                 str(window.problems_panel.tree.topLevelItemCount()))
    checks.check("the status bar counts them", "error" in window.status_problems.text(),
                 window.status_problems.text())
    if shots:
        os.makedirs(os.path.join(ROOT, "build", "ide"), exist_ok=True)
        window.grab().save(os.path.join(ROOT, "build", "ide", "problems.png"))

    # Fix it again.
    editor.setPlainText(original)
    window.server.did_change(editor.path and _uri(editor.path), original)
    pump(app, 900)
    checks.check("fixing the file clears the problems", len(editor.problems) == 0,
                 str(editor.problems[:2]))

    # -------------------------------------------------------- completion
    editor.setPlainText("let value = math.\n")
    window.server.did_change(_uri(editor.path), editor.toPlainText())
    pump(app, 300)

    cursor = editor.textCursor()
    cursor.movePosition(QTextCursor.End)
    editor.setTextCursor(cursor)
    done = {}

    def on_completion(result):
        done["items"] = result

    window.server.complete(_uri(editor.path), 0, len("let value = math."), on_completion)
    pump(app, 600)
    items = (done.get("items") or {}).get("items", [])
    labels = [item.get("label") for item in items]
    checks.check("completion asks the server for math members",
                 "pi" in labels and "sqrt" in labels, str(labels[:8]))

    # And the popup itself, driven through the editor's own path.
    editor.set_completions(items, len("let value = math."))
    checks.check("the completion popup opens", editor.popup.isVisible() or editor.popup.count() > 0,
                 f"visible={editor.popup.isVisible()} count={editor.popup.count()}")
    if shots and editor.popup.count():
        editor.popup.show()
        pump(app, 100)
        window.grab().save(os.path.join(ROOT, "build", "ide", "completion.png"))
        editor.popup.hide()

    # Completion by typing, end to end: the keystrokes reach the editor, the
    # editor tells the server what the document now says, and the list narrows
    # as the word is typed. Every part of this was broken at some point - the
    # server never saw the edit, so the list described the file on disk.
    # A copy, not the repository's file: closing a modified tab asks to save,
    # and with nobody there to click "Discard" the default button answers. The
    # first version of this test wrote `let value = math.sqrt` over an example.
    scratch = tempfile.mkdtemp(prefix="tern-typing-")
    typed_path = os.path.join(scratch, "typing.tern")
    with open(os.path.join(ROOT, "examples", "fizzbuzz.tern"), encoding="utf-8") as source:
        with open(typed_path, "w", encoding="utf-8") as copy:
            copy.write(source.read())
    window.open_file(typed_path)
    pump(app, 800)
    typed = window._editor()
    typed.setPlainText("")
    typed.setFocus()
    pump(app, 250)
    for character in "let value = math.":
        QTest.keyClick(typed, character)
        pump(app, 30)
    pump(app, 900)

    checks.check("typing reaches the editor", typed.toPlainText() == "let value = math.",
                 repr(typed.toPlainText()))
    checks.check("completion opens on a dot", typed.popup.isVisible(),
                 f"visible={typed.popup.isVisible()} rows={typed.popup.count()}")
    labels = [typed.popup.item(row).data(Qt.UserRole)
              for row in range(typed.popup.count()) if not typed.popup.item(row).isHidden()]
    checks.check("the list is the library's members, not the keywords",
                 "sqrt" in labels and "let" not in labels, str(labels[:8]))

    for character in "sqr":
        QTest.keyClick(typed, character)
        pump(app, 80)
    pump(app, 400)
    remaining = [typed.popup.item(row).data(Qt.UserRole)
                 for row in range(typed.popup.count()) if not typed.popup.item(row).isHidden()]
    checks.check("typing narrows the list without asking the server again",
                 remaining == ["sqrt"], str(remaining))
    checks.check("the current row is the narrowed match",
                 typed.popup.current_label() == "sqrt", str(typed.popup.current_label()))
    QTest.keyClick(typed, Qt.Key_Tab)
    pump(app, 250)
    checks.check("Tab completes the word", typed.toPlainText() == "let value = math.sqrt",
                 repr(typed.toPlainText()))
    checks.check("completing closes the popup", not typed.popup.isVisible())

    # Back to the file the rest of the suite works on: format_code acts on the
    # active tab, not on a particular editor.
    window.tabs_widget.setCurrentWidget(window.tabs[editor.path])
    pump(app, 150)

    # ------------------------------------------------------------- hover
    editor.setPlainText(original)
    window.server.did_change(_uri(editor.path), original)
    pump(app, 300)

    hovered = {}
    window.server.hover(_uri(editor.path), 15, 8, lambda result: hovered.update(result or {}))
    pump(app, 500)
    checks.check("hover returns documentation for a name", bool(hovered),
                 str(hovered)[:120])

    # ------------------------------------------------------------- run
    window.console.clear()
    window.run_current()
    pump(app, 4000)
    output = window.console.output.toPlainText()
    checks.check("Run executes the file through the interpreter",
                 "exited with" in output, output[-200:])
    checks.check("the program's own output appears", len(output.strip()) > 0, output[:200])
    if shots:
        window.grab().save(os.path.join(ROOT, "build", "ide", "run.png"))

    # Ctrl+C in the Run window has to stop the program. It used to do nothing:
    # the output is a read-only text view, which swallows it. Driven through a
    # real key event on the widget, because calling stop_run() directly would
    # test the thing that already worked.
    spin = os.path.join(tempfile.mkdtemp(prefix="tern-spin-"), "spin.tern")
    with open(spin, "w", encoding="utf-8") as handle:
        handle.write("let total = 0\nfor n in 1..1000000000 {\n    total = total + n\n}\nprintln(total)\n")
    window.open_file(spin)
    pump(app, 600)
    window.run_current()
    pump(app, 1500)
    checks.check("a long program is still running",
                 window.run_process is not None and
                 window.run_process.state() != QProcess.NotRunning,
                 str(window.run_process.state() if window.run_process else None))
    checks.check("the Stop button is available while it runs",
                 window.console.stop_button.isEnabled(),
                 str(window.console.stop_button.isEnabled()))
    QTest.keyClick(window.console.output, Qt.Key_C, Qt.ControlModifier)
    pump(app, 1200)
    checks.check("Ctrl+C in the Run window stops it", window.run_process is None,
                 str(window.run_process))
    checks.check("and the window says it stopped", "stopped" in window.console.status.text(),
                 window.console.status.text())
    checks.check("the Stop button goes back to disabled",
                 not window.console.stop_button.isEnabled())

    # Copy still wins when there is a selection: that is the other Ctrl+C.
    window.console.output.setPlainText("some output to copy")
    cursor = window.console.output.textCursor()
    cursor.select(cursor.SelectionType.Document)
    window.console.output.setTextCursor(cursor)
    checks.check("a selection is left for Ctrl+C to copy",
                 window.console.output.textCursor().hasSelection())
    window.console.output.moveCursor(cursor.MoveOperation.End)
    window.tabs_widget.setCurrentWidget(window.tabs[editor.path])
    pump(app, 150)

    # ------------------------------------------------------------ theme
    window.set_theme("light")
    checks.check("the light theme applies", window.theme_name == "light")
    checks.check("the editor follows the theme", window._editor().theme is THEMES["light"])
    if shots:
        window.grab().save(os.path.join(ROOT, "build", "ide", "light.png"))
    window.set_theme("darcula")
    checks.check("the dark theme applies back", window.theme_name == "darcula")

    # Every palette must apply, and apply differently: a theme table that
    # silently falls back to one look is worse than no theme menu.
    for name in ("eclipse", "vs-dark", "carbon"):
        window.set_theme(name)
        checks.check(f"the {name} theme applies", window.theme_name == name)
        checks.check(f"the {name} theme reaches the editor",
                     window._editor().theme is THEMES[name])
    window.set_theme("vs-dark")
    dark_css = app.styleSheet()
    window.set_theme("eclipse")
    checks.check("switching themes rewrites the stylesheet", app.styleSheet() != dark_css)
    checks.check("the theme menu has an entry per palette",
                 all(f"theme_{name}" in window.actions_map for name in THEMES),
                 str(sorted(n for n in window.actions_map if n.startswith("theme_"))))
    checks.check("the carbon theme is darker than darcula",
                 _luminance(THEMES["carbon"]["bg"]) < _luminance(THEMES["darcula"]["bg"]),
                 f"{THEMES['carbon']['bg']} vs {THEMES['darcula']['bg']}")

    # Clicking the menu, which is not the same thing as calling set_theme.
    # A checkable QAction hands its checked flag to the slot, so
    # `lambda n=name:` received True, set_theme refused an unknown name, and
    # Qt had already ticked the item: the palette never moved and every entry
    # the user clicked stayed ticked. Both symptoms came from this path, and
    # only this path — calling set_theme directly skipped it.
    window.set_theme("darcula")
    before_css = app.styleSheet()
    window.actions_map["theme_carbon"].trigger()
    pump(app, 200)
    checks.check("clicking a theme in the menu applies it", window.theme_name == "carbon",
                 window.theme_name)
    checks.check("clicking a theme in the menu restyles the window",
                 app.styleSheet() != before_css)
    ticked = [name for name in THEMES if window.actions_map[f"theme_{name}"].isChecked()]
    checks.check("the theme menu is a radio list, not checkboxes", ticked == ["carbon"], str(ticked))

    window.actions_map["theme_light"].trigger()
    pump(app, 200)
    ticked = [name for name in THEMES if window.actions_map[f"theme_{name}"].isChecked()]
    checks.check("a second click moves the tick instead of adding one",
                 window.theme_name == "light" and ticked == ["light"], str(ticked))
    checks.check("the editor follows a menu click", window._editor().theme is THEMES["light"])
    window.set_theme("darcula")

    # ------------------------------------------------------------ format
    editor.setPlainText("fn f() {\nlet x = 1;\nif (x > 0) {\nreturn x;\n}\nreturn 0;\n}\n")
    window.server.did_change(_uri(editor.path), editor.toPlainText())
    pump(app, 300)
    window.format_code()
    pump(app, 600)
    formatted = editor.toPlainText()
    checks.check("Reformat indents the body", "\n    let x = 1;" in formatted, repr(formatted))
    checks.check("Reformat indents the nested block", "\n        return x;" in formatted, repr(formatted))

    # ------------------------------------------------------------- tabs
    other = os.path.join(ROOT, "examples", "fizzbuzz.tern")
    if os.path.exists(other):
        window.open_file(other)
        pump(app, 500)
        checks.check("a second file opens in a tab", len(window.tabs) >= 2, str(len(window.tabs)))
        checks.check("the new tab is active", os.path.basename(window.path_label.text()) == "fizzbuzz.tern",
                     window.path_label.text())

        window.tabs_widget.setCurrentIndex(window.tabs_widget.indexOf(window.tabs[editor.path]))
        pump(app, 200)

    # ----------------------------------------------------------- editing
    editor.setPlainText("let a = 1;\nlet b = 2;\nlet c = 3;\n")
    window.server.did_change(_uri(editor.path), editor.toPlainText())
    pump(app, 200)

    cursor = editor.textCursor()
    cursor.setPosition(0)
    editor.setTextCursor(cursor)
    window.toggle_comment()
    checks.check("Ctrl+/ comments the line", editor.toPlainText().startswith("// let a = 1;"),
                 editor.toPlainText().splitlines()[0])
    window.toggle_comment()
    checks.check("Ctrl+/ uncomments it again", editor.toPlainText().startswith("let a = 1;"),
                 editor.toPlainText().splitlines()[0])

    cursor = editor.textCursor()
    cursor.setPosition(0)
    editor.setTextCursor(cursor)
    window.duplicate_line()
    lines = editor.toPlainText().splitlines()
    checks.check("Ctrl+D duplicates the line", lines[0] == "let a = 1;" and lines[1] == "let a = 1;",
                 str(lines[:3]))

    cursor = editor.textCursor()
    cursor.setPosition(0)
    editor.setTextCursor(cursor)
    window.move_lines(1)
    lines = editor.toPlainText().splitlines()
    checks.check("Alt+Shift+Down moves the line", lines[1] == "let a = 1;" and lines[0] == "let a = 1;"
                 and lines[2] == "let b = 2;", str(lines[:3]))

    # The find bar: open with a seeded term, count the matches, replace them.
    editor.setPlainText("alpha beta alpha gamma\n")
    area = window._area()
    area.find_bar.open(term="alpha")
    pump(app, 100)
    checks.check("the find bar opens with the term", area.find_bar.isVisible()
                 and area.find_bar.find_field.text() == "alpha")
    checks.check("the find bar counts the matches", "2 matches" in area.find_bar.count.text(),
                 area.find_bar.count.text())
    area.find_bar.replace_field.setText("delta")
    replaced = area.find_bar.replace_all()
    checks.check("Replace all rewrites every match", replaced == 2
                 and editor.toPlainText() == "delta beta delta gamma\n", editor.toPlainText())
    area.find_bar.hide()

    # Recent files: the ones opened this session, filtered by typing.
    from ide.Studio.panels import RecentFilesDialog
    recent = RecentFilesDialog([editor.path, other], THEMES["darcula"])
    recent.filter.setText("fizzbuzz")
    checks.check("recent files filters as you type", recent.list.count() == 1,
                 str(recent.list.count()))
    checks.check("recent files carries the path", recent.list.currentItem().data(Qt.UserRole) == other,
                 str(recent.list.currentItem().data(Qt.UserRole)))
    recent.deleteLater()

    # --------------------------------------------------------- actions
    # A shortcut with no handler is a dead key, which is worse than no shortcut
    # at all. Trigger every action and make sure nothing raises: the dock ones
    # change visibility, the file ones open dialogs, which are not triggered
    # here (they would block waiting for a human).
    # Anything that opens a modal dialog would block the suite: those are
    # covered by the checks that call their bodies directly.
    blocking = {"new", "open", "open_recent", "recent", "save", "save_all", "close_tab",
                "undo", "redo", "select_all", "cut", "copy", "paste", "exit",
                "goto_file", "goto_line", "docs", "about", "keymap", "goto_symbol",
                "goto_symbol_in_file", "find_usages", "rename", "check", "debug",
                "compile", "run_in_terminal", "breakpoint_condition", "add_watch"}
    raised = []
    for name, action in window.actions_map.items():
        if name in blocking or not action.shortcut().toString():
            continue
        try:
            action.trigger()
            pump(app, 20)
        except Exception as error:                     # noqa: BLE001 - reported below
            raised.append(f"{name}: {error}")
    checks.check("every shortcut does something without failing", raised == [], str(raised[:3]))
    if window._area() is not None:
        window._area().find_bar.hide()
    window.tabs_widget.setCurrentWidget(window.tabs[editor.path])
    pump(app, 200)

    # ------------------------------------------------------ signature help
    # Type inside a real call and read the hint that appears above the caret.
    editor = window._editor()
    editor.setPlainText("fn classify(line, rule) {\n    return line;\n}\n\nlet first = classify(\"x\", second);\n"
                        "let second = 2;\n")
    window.server.did_change(_uri(editor.path), editor.toPlainText())
    pump(app, 400)
    # The call, not the declaration: the first `classify(` is the `fn` line.
    cursor = editor.textCursor()
    call = editor.toPlainText().index('classify("') + len("classify(")
    cursor.setPosition(call)
    editor.setTextCursor(cursor)
    pump(app, 1400)
    hint = getattr(window, "signature_label", None)
    checks.check("the parameter hint appears inside a call",
                 hint is not None and hint.isVisible(), "no hint")
    # Above the caret's own line, or it would cover the code being typed.
    caret_top = editor.cursorRect().top()
    checks.check("the hint sits at the caret", hint is not None and
                 abs(hint.geometry().top() - window.mapFromGlobal(editor.viewport().mapToGlobal(
                     editor.cursorRect().bottomLeft())).y()) <= 2,
                 f"hint {hint.geometry().top()} vs caret {caret_top}")
    checks.check("the hint names the function and its parameters",
                 hint is not None and "classify" in hint.text() and "line" in hint.text(),
                 hint.text() if hint else "none")

    cursor.clearSelection()
    cursor.setPosition(0)
    editor.setTextCursor(cursor)
    pump(app, 600)
    checks.check("the hint goes away outside a call",
                 hint is not None and not hint.isVisible(), "still visible")

    # ---------------------------------------------------------- terminal
    # The Terminal is a shell, so the checks run real commands through it.
    terminal = window.terminal
    terminal.clear()
    terminal.input.setText("echo from-the-terminal")
    terminal.submit()
    pump(app, 900)
    output = terminal.output.toPlainText()
    checks.check("the terminal runs a command", "from-the-terminal" in output, output[-160:])
    checks.check("the command is echoed as a prompt line", "$ echo from-the-terminal" in output,
                 output[-200:])

    scratch = tempfile.mkdtemp(prefix="tern-term-")
    terminal.input.setText(f"cd {scratch} && pwd")
    terminal.submit()
    pump(app, 900)
    checks.check("cd persists in the shell", os.path.realpath(terminal.cwd) == os.path.realpath(scratch),
                 f"{terminal.cwd} != {scratch}")
    checks.check("the prompt follows the directory", scratch.split(os.sep)[-1] in terminal.prompt.text(),
                 terminal.prompt.text())

    terminal.input.setText("false")
    terminal.submit()
    pump(app, 800)
    checks.check("a failing status reaches the toolbar", "exit 1" in terminal.status.text(),
                 terminal.status.text())

    # `exit` ends the shell; the panel says so, and the next command starts a
    # new one rather than typing into a corpse.
    terminal.input.setText("exit 3")
    terminal.submit()
    pump(app, 900)
    checks.check("closing the shell is reported", "exited" in terminal.status.text(),
                 terminal.status.text())
    terminal.input.setText("echo back-again")
    terminal.submit()
    pump(app, 1200)
    checks.check("the next command restarts the shell",
                 "back-again" in terminal.output.toPlainText(),
                 terminal.output.toPlainText()[-140:])

    # Colours: ask for red, then read the format back out of the document.
    terminal.clear()
    terminal.input.setText("printf '\\033[31mALARM\\033[0m\\n'")
    terminal.submit()
    pump(app, 900)
    document = terminal.output.document()
    reds = []
    block = document.begin()
    while block.isValid():
        iterator = block.begin()
        while not iterator.atEnd():
            fragment = iterator.fragment()
            if fragment.isValid() and "ALARM" in fragment.text():
                reds.append(fragment.charFormat().foreground().color().name())
            iterator += 1
        block = block.next()
    checks.check("ANSI colour is applied to output", reds and reds[0].lower() != "#a9b7c6",
                 str(reds))

    # The menu entry: no dialog, just the command in the dock's shell.
    window.run_command_in_terminal("echo from-the-menu")
    pump(app, 1200)
    checks.check("Ctrl+Alt+T runs a command in the terminal",
                 "from-the-menu" in terminal.output.toPlainText(),
                 terminal.output.toPlainText()[-120:])
    checks.check("the terminal window is showing", window.terminal_dock.isVisible(),
                 str(window.terminal_dock.isVisible()))

    # Ctrl+C on a sleeping command: the shell survives and takes the next one.
    terminal.input.setText("sleep 30")
    terminal.submit()
    pump(app, 700)
    terminal.interrupt()
    pump(app, 900)
    terminal.input.setText("echo after-interrupt")
    terminal.submit()
    pump(app, 900)
    checks.check("Ctrl+C stops the running command",
                 "after-interrupt" in terminal.output.toPlainText(),
                 terminal.output.toPlainText()[-140:])

    # Up-arrow history: the last command comes back to the prompt.
    for command in ("echo first-one", "echo second-one"):
        terminal.input.setText(command)
        terminal.submit()
        pump(app, 600)
    terminal.input.setFocus()
    QTest.keyClick(terminal.input, Qt.Key_Up)
    pump(app, 150)
    recalled = terminal.input.text()
    checks.check("Up recalls the previous command", recalled == "echo second-one", recalled)
    QTest.keyClick(terminal.input, Qt.Key_Up)
    pump(app, 150)
    checks.check("Up again walks further back", terminal.input.text() == "echo first-one",
                 terminal.input.text())
    terminal.input.clear()

    # Ctrl+C from the output, not the prompt box. Clicking into the scrollback
    # to read a command is normal, and the keyboard follows the click; the
    # filter used to be installed on the prompt only, so from there Ctrl+C did
    # nothing at all.
    terminal.clear()
    terminal.input.setText("sleep 30")
    terminal.submit()
    pump(app, 700)
    running_before = terminal.process.state() != QProcess.NotRunning
    checks.check("a long terminal command is running", running_before,
                 str(terminal.process.state()))
    terminal.output.setFocus()
    QTest.keyClick(terminal.output, Qt.Key_C, Qt.ControlModifier)
    pump(app, 1500)
    checks.check("Ctrl+C in the terminal output interrupts the command",
                 "^C" in terminal.output.toPlainText(), repr(terminal.output.toPlainText()[-120:]))
    terminal.input.setText("echo after-ctrl-c")
    terminal.submit()
    pump(app, 1200)
    checks.check("the shell still works after Ctrl+C",
                 "after-ctrl-c" in terminal.output.toPlainText(),
                 terminal.output.toPlainText()[-120:])

    # Ctrl+L clears the view, as in a shell.
    QTest.keyClick(terminal.input, Qt.Key_L, Qt.ControlModifier)
    pump(app, 200)
    checks.check("Ctrl+L clears the terminal", terminal.output.toPlainText().strip() == "",
                 repr(terminal.output.toPlainText()[:60]))

    terminal.restart()
    pump(app, 900)
    terminal.input.setText("echo after-restart")
    terminal.submit()
    pump(app, 900)
    checks.check("the shell restarts and takes commands", "after-restart" in terminal.output.toPlainText(),
                 terminal.output.toPlainText()[-120:])
    shutil_quiet(scratch)
    terminal.cwd = ROOT
    terminal.restart()
    pump(app, 500)

    # ------------------------------------------------------- Windows paths
    # Nothing here needs Windows: these are the pure functions the IDE uses for
    # file URIs, and getting them wrong is a bug that only shows up on somebody
    # else's machine.
    from ide.Studio.paths import normalise_uri_path, path_for

    checks.check("a Windows path becomes a URI path",
                 normalise_uri_path("C:\\Users\\thoma\\a.tern") == "/C:/Users/thoma/a.tern",
                 normalise_uri_path("C:\\Users\\thoma\\a.tern"))
    checks.check("a POSIX path is left as it is",
                 normalise_uri_path("/home/user/a.tern") == "/home/user/a.tern",
                 normalise_uri_path("/home/user/a.tern"))
    checks.check("a Windows file URI comes back as a drive path",
                 path_for("file:///C:/Users/thoma/a.tern") == "C:/Users/thoma/a.tern",
                 path_for("file:///C:/Users/thoma/a.tern"))
    checks.check("a POSIX file URI keeps its leading slash",
                 path_for("file:///home/user/a.tern") == "/home/user/a.tern",
                 path_for("file:///home/user/a.tern"))
    checks.check("a bare path is its own URI path",
                 path_for("file://server/share/a.tern") == "server/share/a.tern",
                 path_for("file://server/share/a.tern"))

    # The runtime resolver: the bundled launcher is preferred, and DOTNET_ROOT
    # is only set for it, never for a system SDK.
    from ide.Studio import runtime

    bundled = runtime.command(ROOT, "TERN.Ide")
    checks.check("the syntax server is found where the build put it",
                 bundled is not None and bundled[1].endswith("TERN.Ide.dll"),
                 str(bundled))
    checks.check("the bundled runtime gets DOTNET_ROOT",
                 "DOTNET_ROOT" in runtime.environment(ROOT, bundled[0]) if bundled else False,
                 str(runtime.environment(ROOT, bundled[0]) if bundled else None))
    checks.check("a system dotnet does not",
                 runtime.environment(ROOT, "/usr/bin/dotnet") == {},
                 str(runtime.environment(ROOT, "/usr/bin/dotnet")))

    # --------------------------------------------------- finding a virtualenv
    # The launcher searched only the repository root, so a venv made where the
    # user happened to be standing — tools\.venv, say — "did not exist".
    import importlib.util
    from pathlib import Path

    spec = importlib.util.spec_from_file_location(
        "studio_launcher", os.path.join(ROOT, "tools", "studio.py"))
    launcher = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(launcher)

    nested = tempfile.mkdtemp(prefix="tern-venv-")
    os.makedirs(os.path.join(nested, "inner"), exist_ok=True)
    roots = launcher.search_roots(Path(nested))
    checks.check("the directory the launcher ran from is searched first",
                 roots[0] == Path(nested), str(roots[:3]))
    checks.check("the repository root is searched too",
                 Path(ROOT) in roots, str(roots[:4]))

    fake = Path(nested) / ".venv" / ("Scripts" if os.name == "nt" else "bin")
    fake.mkdir(parents=True)
    (fake / ("python.exe" if os.name == "nt" else "python")).write_text("")
    checks.check("a virtualenv beside the launcher is found",
                 str(fake / ("python.exe" if os.name == "nt" else "python"))
                 in launcher.venv_pythons(Path(nested)),
                 str(launcher.venv_pythons(Path(nested))[:3]))

    # ------------------------------------------------------ find in path
    window.show_find_in_path("classify")
    pump(app, 700)
    checks.check("Find in Path searches the project",
                 window.find_panel.results.topLevelItemCount() > 0,
                 window.find_panel.summary.text())
    checks.check("the summary counts the matches", "match" in window.find_panel.summary.text(),
                 window.find_panel.summary.text())

    first_hit = window.find_panel.results.topLevelItem(0)
    hit_path, hit_line, hit_column = first_hit.data(0, Qt.UserRole)
    window._goto_line_column(hit_path, hit_line, hit_column)
    pump(app, 400)
    checks.check("a match opens at its line",
                 window._editor() is not None
                 and window._editor().path == hit_path
                 and window._editor().textCursor().blockNumber() + 1 == hit_line,
                 f"{hit_path}:{hit_line}")
    window.tabs_widget.setCurrentWidget(window.tabs[editor.path])
    pump(app, 200)

    # A non-TERN file opens as plain text and is not handed to the analyser.
    markdown = os.path.join(ROOT, "docs", "language.md")
    window.open_file(markdown)
    pump(app, 600)
    markdown_editor = window._editor()
    checks.check("a Markdown file opens", markdown_editor is not None
                 and markdown_editor.path == markdown)
    checks.check("a non-TERN file gets no diagnostics", markdown_editor.problems == [],
                 str(markdown_editor.problems[:3]))
    window.tabs_widget.setCurrentWidget(window.tabs[editor.path])
    pump(app, 200)

    # -------------------------------------------------------- debugging
    # A breakpoint, a stop, the stack, the locals, a step, and the program's
    # output — through the window, not the protocol client.
    debug_root = tempfile.mkdtemp(prefix="tern-debug-ui-")
    debug_file = os.path.join(debug_root, "counted.tern")
    with open(debug_file, "w", encoding="utf-8") as handle:
        handle.write("fn double(n) {\n    let twice = n * 2;\n    return twice;\n}\n\n"
                     "let total = 0;\nfor n in 1..4 {\n    total = total + double(n);\n}\n"
                     "println(total);\n")

    debug_window = StudioWindow(debug_root, theme="darcula")
    debug_window.resize(1440, 900)
    debug_window.show()
    pump(app, 2000)
    debug_window.open_file(debug_file)
    pump(app, 500)

    debug_editor = debug_window._editor()
    set_breakpoint = debug_editor.toggle_breakpoint(1)      # line 2, inside double
    checks.check("a breakpoint can be set on a line", set_breakpoint is True
                 and 2 in debug_editor.breakpoints, str(debug_editor.breakpoints))
    cleared = debug_editor.toggle_breakpoint(1)
    checks.check("a breakpoint can be cleared again", cleared is False
                 and 2 not in debug_editor.breakpoints, str(debug_editor.breakpoints))
    debug_editor.toggle_breakpoint(1)

    # A condition on the breakpoint, set through the window: still a stop at
    # line 2, but only when the argument is 3 or more. A watch rides along.
    debug_window.set_breakpoint_condition(1, "n > 2")
    checks.check("a condition can be attached to a breakpoint",
                 debug_editor.breakpoint_conditions.get(2) == "n > 2",
                 str(debug_editor.breakpoint_conditions))
    checks.check("the breakpoint spec carries the condition",
                 {"line": 2, "condition": "n > 2"} in debug_editor.breakpoint_specs(),
                 str(debug_editor.breakpoint_specs()))

    debug_window.debug_current()
    pump(app, 3000)

    checks.check("Debug starts a session", debug_window.debug_session is not None)
    checks.check("the condition held on the first stop (n > 2 means n is 3)",
                 "n" in {debug_window.debug_panel.variables.topLevelItem(i).text(0)
                         for i in range(debug_window.debug_panel.variables.topLevelItemCount())},
                 "no n")
    checks.check("the program stops at the breakpoint",
                 debug_window.debug_panel.state.text().startswith("Paused on breakpoint"),
                 debug_window.debug_panel.state.text())
    frame_names = [debug_window.debug_panel.frames.topLevelItem(i).text(0)
                   for i in range(debug_window.debug_panel.frames.topLevelItemCount())]
    checks.check("the Debug window shows the stack", frame_names[:2] == ["double", "<module>"],
                 str(frame_names))

    def variable_map():
        panel = debug_window.debug_panel
        return {panel.variables.topLevelItem(i).text(0): panel.variables.topLevelItem(i).text(1)
                for i in range(panel.variables.topLevelItemCount())}

    values = variable_map()
    checks.check("the Debug window shows the parameters", values.get("n") in ("1", "2", "3"),
                 str(values))

    debug_window.add_watch("n")
    debug_window.debug_panel.set_watch_values({})
    debug_window._refresh_watches()
    pump(app, 600)
    watch_value = debug_window.debug_panel.watches.topLevelItem(0).text(1)
    checks.check("a watch is evaluated at the stop", watch_value == "3", watch_value)

    debug_window.debug_step_over()
    pump(app, 900)
    checks.check("Step Over advances the line",
                 debug_window.debug_panel.state.text().startswith("Paused on step"),
                 debug_window.debug_panel.state.text())
    values = variable_map()
    checks.check("the stepped-over locals are still shown", "n" in values, str(values))

    # The loop calls `double` four times, so the breakpoint fires again; clear it
    # before letting the program finish.
    debug_editor.toggle_breakpoint(1)
    debug_window.debug_resume()
    pump(app, 3000)
    checks.check("the program finishes and the panel says so",
                 debug_window.debug_panel.state.text() in ("Program finished", "Not debugging"),
                 debug_window.debug_panel.state.text())
    checks.check("the program's own output arrives in the Run window",
                 "20" in debug_window.console.output.toPlainText(),
                 debug_window.console.output.toPlainText()[-100:])
    checks.check("the stopped marker is cleared", debug_editor.stopped_line == -1,
                 str(debug_editor.stopped_line))

    if shots:
        os.makedirs(os.path.join(ROOT, "docs", "ide", "img"), exist_ok=True)
        debug_editor.toggle_breakpoint(1) if 2 not in debug_editor.breakpoints else None
        # A conditioned breakpoint and a watch, so the picture shows both.
        debug_window.set_breakpoint_condition(1, "n > 2")
        debug_window.add_watch("n")
        debug_window.add_watch("twice")
        debug_window.debug_current()
        pump(app, 3000)
        debug_window._show_dock(debug_window.debug_dock)
        pump(app, 300)
        debug_window.grab().save(os.path.join(ROOT, "docs", "ide", "img", "debug.png"))
        print("       wrote docs/ide/img/debug.png")
        debug_window.debug_resume()
        pump(app, 1500)

    debug_window._shutdown()
    pump(app, 300)
    debug_window.deleteLater()
    shutil_quiet(debug_root)

    # -------------------------------------------------------------- VCS
    # A throwaway repository: the panel is exercised against real git output,
    # including a commit, without touching the project under test.
    import shutil
    import subprocess

    scratch = tempfile.mkdtemp(prefix="tern-vcs-")

    def git(*arguments):
        return subprocess.run(["git", *arguments], cwd=scratch, capture_output=True,
                              text=True, timeout=30)

    git("init", "-q", "-b", "main")
    git("config", "user.name", "TERN Test")
    git("config", "user.email", "test@example.com")
    git("config", "commit.gpgsign", "false")

    with open(os.path.join(scratch, "hello.tern"), "w", encoding="utf-8") as handle:
        handle.write('println("hello");\n')
    git("add", "hello.tern")
    git("commit", "-q", "-m", "first commit")

    with open(os.path.join(scratch, "hello.tern"), "w", encoding="utf-8") as handle:
        handle.write('println("hello, world");\n')
    with open(os.path.join(scratch, "extra.tern"), "w", encoding="utf-8") as handle:
        handle.write('println("new file");\n')

    vcs_window = StudioWindow(scratch, theme="darcula")
    vcs_window.resize(1280, 800)
    vcs_window.show()
    pump(app, 2200)

    panel = vcs_window.changes_panel
    checks.check("the Commit window finds the repository", panel.repository is not None)
    checks.check("the branch name is shown", panel.header.text().startswith("main"),
                 panel.header.text())

    listed = {panel.tree.topLevelItem(i).data(0, Qt.UserRole)
              for i in range(panel.tree.topLevelItemCount())}
    checks.check("a modified file is listed", "hello.tern" in listed, str(sorted(listed)))
    checks.check("an untracked file is listed", "extra.tern" in listed, str(sorted(listed)))

    letters = {panel.tree.topLevelItem(i).data(0, Qt.UserRole):
               panel.tree.topLevelItem(i).data(0, Qt.UserRole + 1)
               for i in range(panel.tree.topLevelItemCount())}
    checks.check("the status letter is right", letters.get("hello.tern") == "M"
                 and letters.get("extra.tern") == "?", str(letters))

    vcs_window.show_diff(os.path.join(scratch, "hello.tern"))
    pump(app, 300)
    diff_text = vcs_window.tabs_widget.currentWidget().view.toPlainText()
    checks.check("the diff shows the removed line", '-println("hello");' in diff_text, diff_text[:200])
    checks.check("the diff shows the added line",
                 '+println("hello, world");' in diff_text, diff_text[:200])
    checks.check("a diff tab clears the structure view",
                 vcs_window.structure_panel.tree.topLevelItemCount() == 0,
                 str(vcs_window.structure_panel.tree.topLevelItemCount()))

    if shots:
        os.makedirs(os.path.join(ROOT, "docs", "ide", "img"), exist_ok=True)
        vcs_window.resize(1440, 900)
        panel.refresh()
        vcs_window._show_dock(vcs_window.changes_dock)
        vcs_window.tabs_widget.setCurrentWidget(vcs_window.diff_tabs["work:" + os.path.join(scratch, "hello.tern")])
        pump(app, 400)
        vcs_window.grab().save(os.path.join(ROOT, "docs", "ide", "img", "commit.png"))
        print("       wrote docs/ide/img/commit.png")

    panel.message.setPlainText("change the greeting")
    ok, output = panel.commit()
    pump(app, 600)
    checks.check("Commit stages and commits", ok, output[-200:])
    checks.check("the file list is empty afterwards", panel.tree.topLevelItemCount() == 0,
                 str(panel.tree.topLevelItemCount()))
    checks.check("the commit message was cleared", panel.message.toPlainText() == "")

    log = subprocess.run(["git", "log", "-1", "--pretty=%s"], cwd=scratch,
                         capture_output=True, text=True).stdout.strip()
    checks.check("git recorded the commit", log == "change the greeting", log)

    vcs_window._shutdown()
    pump(app, 300)
    vcs_window.deleteLater()
    shutil.rmtree(scratch, ignore_errors=True)

    # A directory that is not a repository says so instead of failing.
    plain = tempfile.mkdtemp(prefix="tern-plain-")
    plain_window = StudioWindow(plain, theme="darcula")
    pump(app, 1200)
    checks.check("a non-repository is reported, not crashed",
                 plain_window.changes_panel.repository is None
                 and "Not a git repository" in plain_window.changes_panel.header.text(),
                 plain_window.changes_panel.header.text())
    plain_window._shutdown()
    pump(app, 200)
    plain_window.deleteLater()
    shutil.rmtree(plain, ignore_errors=True)

    # --------------------------------------------------------- settings
    # What a second launch sees: the theme, the geometry and the open files
    # from the session that just ended. An ini file stands in for the real
    # registry/config path so the check never touches the user's settings.
    from PySide6.QtCore import QSettings
    from ide.Studio.window import StudioWindow

    settings_path = os.path.join(tempfile.mkdtemp(prefix="tern-studio-"), "studio.ini")
    window.settings = QSettings(settings_path, QSettings.IniFormat)
    window.set_theme("light")
    window.resize(1200, 760)
    window._remember()

    second = StudioWindow(ROOT, theme="darcula",
                          settings=QSettings(settings_path, QSettings.IniFormat))
    second.resize(1200, 760)
    second.show()
    pump(app, 1800)

    checks.check("a new session restores the theme", second.theme_name == "light",
                 second.theme_name)
    checks.check("a new session restores the open files",
                 set(second.tabs) == set(window.tabs),
                 f"{sorted(os.path.basename(p) for p in second.tabs)}")
    checks.check("the restored session has a working syntax server",
                 second.status_server.text() == "syntax server: ready",
                 second.status_server.text())
    checks.check("the restored outline is populated", second.structure_panel.tree.topLevelItemCount() > 0,
                 str(second.structure_panel.tree.topLevelItemCount()))
    second._shutdown()
    pump(app, 300)
    second.deleteLater()
    # Let Qt actually destroy the second window before the first one is
    # re-polished: theming while its widgets are half-deleted trips a
    # refcount bug in shiboken, which shows up as a fatal Python error.
    pump(app, 300)
    second = None
    import gc
    gc.collect()
    window.set_theme("darcula")

    # --------------------------------------------------------- screenshots
    if shots:
        os.makedirs(os.path.join(ROOT, "build", "ide"), exist_ok=True)
        window.tabs_widget.setCurrentWidget(window.tabs[editor.path])
        editor.setPlainText(original)
        pump(app, 400)
        window.grab().save(os.path.join(ROOT, "build", "ide", "studio.png"))
        print("       wrote build/ide/*.png")

    window._shutdown()
    app.processEvents()

    print()
    print(f"passed: {checks.passed}  failed: {len(checks.failed)}")
    for name in checks.failed:
        print(f"  failed: {name}")
    return 1 if checks.failed else 0


def shutil_quiet(path):
    import shutil
    shutil.rmtree(path, ignore_errors=True)


def _uri(path):
    return "file://" + os.path.abspath(path).replace("\\", "/")


if __name__ == "__main__":
    status = main()
    # Tear the interpreter down deliberately: several windows, editors and
    # highlighter documents are still alive, and letting Python finalise them in
    # arbitrary order makes PySide6 abort during garbage collection. Quitting
    # every object while the Qt application still exists avoids that.
    import gc
    gc.collect()
    sys.stdout.flush()
    sys.stderr.flush()
    os._exit(status)
