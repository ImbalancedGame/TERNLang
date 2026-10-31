#!/usr/bin/env python3
"""Writes the UI screenshots used in the documentation.

Each shot opens the window, puts it into a state that shows one feature, and
grabs it. They are generated rather than hand-taken so they stay in step with
the code.

    python3 tools/ide/shots.py [output-directory]
"""

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "docs", "ide", "img")

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
sys.path.insert(0, ROOT)
from ide.Studio.runtime import environment as _runtime_environment  # noqa: E402

for _name, _value in _runtime_environment(ROOT).items():
    os.environ.setdefault(_name, _value)


def pump(app, milliseconds):
    from PySide6.QtCore import QEventLoop, QTimer
    loop = QEventLoop()
    QTimer.singleShot(milliseconds, loop.quit)
    loop.exec()
    app.processEvents()


def main():
    from PySide6.QtWidgets import QApplication
    from PySide6.QtGui import QTextCursor

    app = QApplication(["tern-studio-shots"])
    from ide.Studio.theme import THEMES, apply_to, stylesheet
    from ide.Studio.window import StudioWindow

    apply_to(app)
    app.setStyleSheet(stylesheet(THEMES["darcula"]))
    os.makedirs(OUT, exist_ok=True)

    window = StudioWindow(ROOT, theme="darcula")
    window.resize(1440, 900)
    window.show()
    pump(app, 2200)

    def save(name):
        pump(app, 250)
        window.grab().save(os.path.join(OUT, name))
        print(f"  {name}")

    # 1. The IDE as it opens.
    save("studio.png")

    # 2. A broken file: diagnostics in the gutter, the Problems window and the
    #    status bar.
    area = window._area()
    editor = area.editor
    original = editor.toPlainText()
    uri = "file://" + os.path.abspath(editor.path)

    editor.setPlainText(original.replace("let log = [", "let log = [;;;"))
    window.server.did_change(uri, editor.toPlainText())
    pump(app, 1200)
    window._show_dock(window.problems_dock)
    save("diagnostics.png")

    # 3. Completion after a namespace, popup open.
    editor.setPlainText("import \"std:math\";\n\nlet angle = math.\n")
    window.server.did_change(uri, editor.toPlainText())
    pump(app, 400)
    cursor = editor.textCursor()
    cursor.movePosition(QTextCursor.End)
    editor.setTextCursor(cursor)

    held = {}

    def arrived(result):
        held["items"] = result

    window.server.complete(uri, 2, len("let angle = math."), arrived)
    pump(app, 800)
    items = (held.get("items") or {}).get("items", [])
    editor.set_completions(items, len("import \"std:math\";\n\nlet angle = math.") - 1)
    editor.popup.show()
    pump(app, 300)

    # A Qt popup is a top-level window, so it is not in the main window's grab.
    # Compose the two: the window, then the popup where it actually sits.
    from PySide6.QtGui import QPainter
    from PySide6.QtCore import QPoint

    frame = window.grab()
    popup_image = editor.popup.grab()
    painter = QPainter(frame)
    painter.drawPixmap(editor.popup.mapToGlobal(QPoint(0, 0)) - window.mapToGlobal(QPoint(0, 0)),
                       popup_image)
    painter.end()
    frame.save(os.path.join(OUT, "completion.png"))
    print("  completion.png")
    editor.popup.hide()

    # 4. The find bar over the editor, with a count.
    editor.setPlainText(original)
    window.server.did_change(uri, editor.toPlainText())
    pump(app, 300)
    area = window._area()
    area.find_bar.open(with_replace=True, term="rule")
    pump(app, 250)
    save("find.png")
    area.find_bar.hide()

    # 5. The Run window with a program's output.
    editor.setPlainText(original)
    window.server.did_change(uri, editor.toPlainText())
    pump(app, 400)
    window._show_dock(window.console_dock)
    window.console.clear()
    window.run_current()
    pump(app, 4500)
    save("run.png")

    # 6. Terminal: a couple of commands with coloured output.
    window._show_dock(window.terminal_dock)
    window.terminal.clear()
    for command in ("ls examples",
                    "grep -c 'Rule(' examples/triage.tern",
                    "printf '\\033[32mpass\\033[0m: examples/triage.tern\\n'"):
        window.terminal.input.setText(command)
        window.terminal.submit()
        pump(app, 450)
    save("terminal.png")
    window._show_dock(window.console_dock)

    # 7. Signature help while typing a call.
    editor = window._area().editor
    editor.setPlainText("fn classify(line, rule) {\n    return rule;\n}\n\n"
                        "let rules = [1];\nlet found = classify(\"timeout\", rules[0]);\n")
    window.server.did_change(uri, editor.toPlainText())
    pump(app, 400)
    cursor = editor.textCursor()
    cursor.setPosition(editor.toPlainText().index('classify("') + len("classify("))
    editor.setTextCursor(cursor)
    pump(app, 1500)
    save("signature.png")
    cursor.setPosition(0)
    editor.setTextCursor(cursor)
    pump(app, 200)

    # 8. Find in Path, with a Markdown hit opened at its line.
    window.show_find_in_path("Diagnostics print")
    pump(app, 700)
    hits = window.find_panel.results
    chosen = None
    from PySide6.QtCore import Qt as _Qt
    for index in range(hits.topLevelItemCount()):
        candidate = hits.topLevelItem(index).data(0, _Qt.UserRole)
        if candidate[0].endswith(".md"):
            chosen = candidate
            break
    if chosen is None and hits.topLevelItemCount():
        chosen = hits.topLevelItem(0).data(0, _Qt.UserRole)
    if chosen is not None:
        window._goto_line_column(*chosen)
        pump(app, 500)
        save("find-in-path.png")
    window.open_file(editor.path)
    pump(app, 300)

    # 7. The light theme, on the file the tour started with.
    window._area().editor.setPlainText(original)
    window.server.did_change(uri, window._area().editor.toPlainText())
    window.find_dock.hide()
    window._show_dock(window.console_dock)
    pump(app, 500)
    window.set_theme("light")
    pump(app, 400)
    save("light.png")
    window.set_theme("darcula")

    window._shutdown()
    pump(app, 300)
    print(f"wrote {len(os.listdir(OUT))} image(s) to {os.path.relpath(OUT, ROOT)}")
    return 0


if __name__ == "__main__":
    status = main()
    # As in ui_test.py: quit while Qt still exists rather than letting Python
    # finalise the window and its documents in an arbitrary order.
    import gc
    gc.collect()
    sys.stdout.flush()
    os._exit(status)
