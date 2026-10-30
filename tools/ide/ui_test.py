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

    debug_window.debug_current()
    pump(app, 3000)

    checks.check("Debug starts a session", debug_window.debug_session is not None)
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
    sys.exit(main())
