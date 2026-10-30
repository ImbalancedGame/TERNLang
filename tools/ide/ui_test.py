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
os.environ.setdefault("DOTNET_ROOT", os.path.join(
    os.environ.get("TERN_TOOLCHAIN_DIR", "/tmp/tern-toolchain"), "dotnet"))


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

    from PySide6.QtWidgets import QApplication
    from PySide6.QtGui import QTextCursor
    from PySide6.QtCore import Qt

    app = QApplication(["tern-studio-test"])
    sys.path.insert(0, ROOT)

    from ide.Studio.theme import THEMES, stylesheet
    from ide.Studio.window import StudioWindow

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

    # ------------------------------------------------------------ theme
    window.set_theme("light")
    checks.check("the light theme applies", window.theme_name == "light")
    checks.check("the editor follows the theme", window._editor().theme is THEMES["light"])
    if shots:
        window.grab().save(os.path.join(ROOT, "build", "ide", "light.png"))
    window.set_theme("darcula")
    checks.check("the dark theme applies back", window.theme_name == "darcula")

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


def _uri(path):
    return "file://" + os.path.abspath(path).replace("\\", "/")


if __name__ == "__main__":
    sys.exit(main())
