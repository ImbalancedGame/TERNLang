"""Entry point for TERN Studio.

    python3 -m ide.Studio [project-directory] [--theme darcula|light]
                          [--screenshot out.png] [--size 1440x900]
                          [--no-settings]

With no display, `--screenshot` renders the window with Qt's offscreen platform
and exits, which is how the screenshots in the repository are produced and how
the UI is tested on a machine with no desktop. `--no-settings` ignores and does
not write the remembered theme, size and file list, which is what the tests and
the screenshot generator want.
"""

import os
import sys

from PySide6.QtCore import QSettings, QTimer
from PySide6.QtWidgets import QApplication

from .theme import stylesheet, THEMES


def _take_option(argv, name, default=None):
    """Removes `--name value` from argv and returns the value."""
    if name not in argv:
        return default
    index = argv.index(name)
    if index + 1 >= len(argv):
        raise SystemExit(f"{name} needs a value")
    value = argv[index + 1]
    del argv[index:index + 2]
    return value


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)

    theme = _take_option(argv, "--theme", "darcula")
    if theme not in THEMES:
        raise SystemExit(f"unknown theme {theme!r}; use one of {', '.join(THEMES)}")

    screenshot = _take_option(argv, "--screenshot")
    size = _take_option(argv, "--size")
    if size is not None:
        width, _, height = size.partition("x")
        try:
            size = (int(width), int(height))
        except ValueError:
            raise SystemExit(f"--size wants WIDTHxHEIGHT, not {size!r}")

    remember = "--no-settings" not in argv
    argv = [argument for argument in argv if argument != "--no-settings"]

    offscreen = screenshot is not None or "--offscreen" in argv
    if offscreen:
        os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
        argv = [argument for argument in argv if argument != "--offscreen"]

    if len(argv) > 1:
        raise SystemExit(f"unexpected argument(s): {' '.join(argv[1:])}")

    default_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

    app = QApplication(sys.argv[:1] + ["TERN Studio"])
    app.setApplicationName("TERN Studio")
    app.setOrganizationName("TERN")
    app.setStyleSheet(stylesheet(THEMES[theme]))

    settings = QSettings() if remember and not screenshot else None

    # A directory names the project; without one, reopen the last project, and
    # failing that the repository this Studio came from.
    root = argv[0] if argv else None
    if root is None and settings is not None:
        remembered = settings.value("project", "", str)
        if remembered and os.path.isdir(remembered):
            root = remembered
            theme = settings.value("theme", theme, str)
            if theme not in THEMES:
                theme = "darcula"
            app.setStyleSheet(stylesheet(THEMES[theme]))
    if root is None:
        root = default_root

    from .window import StudioWindow

    window = StudioWindow(root, theme=theme, settings=settings)
    if size:
        window.resize(*size)
    window.show()

    if screenshot:
        # Let the event loop run so the syntax server can be started and the
        # first file can be analysed; then grab the window and quit.
        def capture():
            window.grab().save(screenshot)
            print(f"studio: wrote {screenshot}")
            app.quit()

        QTimer.singleShot(2500, capture)

    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
