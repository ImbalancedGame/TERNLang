"""The Terminal tool window: a shell, not a log view.

Rider's terminal is a real terminal, so this is one too: a `bash` process kept
alive for the session, a prompt line that sends it commands, and output drawn
with the colours the command asked for. `cd` persists, environment variables
persist, and Ctrl+C interrupts what is running.

There is no pseudo-terminal here — Qt can allocate one only through a pty pair,
and a plain pipe is enough for the commands a person types into an IDE terminal.
The price is that programs which insist on a tty (full-screen editors, anything
that draws with cursor addressing) will not run; they are better in the system
terminal anyway. Everything else — git, builds, test runners, `ls --color` —
behaves.
"""

import os
import re
import shutil
import subprocess
import time

WINDOWS = os.name == "nt"

from PySide6.QtCore import QProcess, QProcessEnvironment, Qt
from PySide6.QtGui import QColor, QTextCharFormat, QTextCursor
from PySide6.QtWidgets import (QHBoxLayout, QLabel, QLineEdit, QPlainTextEdit,
                               QPushButton, QVBoxLayout, QWidget)

from .theme import monospace

# The standard 16, then a 6x6x6 cube and 24 greys for the 256-colour form.
BASE_COLOURS = [
    "#3b3b3b", "#cc5555", "#55aa55", "#ccaa33", "#4a88c7", "#aa55aa", "#44aabb", "#bbbbbb",
    "#666666", "#ff6b68", "#8ec07c", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#ffffff",
]


def colour_256(index):
    """A 256-colour index to a hex string, as xterm defines the palette."""
    if index < 16:
        return BASE_COLOURS[index]
    if index < 232:
        index -= 16
        levels = [0, 95, 135, 175, 215, 255]
        return "#%02x%02x%02x" % (levels[index // 36], levels[index // 6 % 6], levels[index % 6])
    grey = 8 + (index - 232) * 10
    return "#%02x%02x%02x" % (grey, grey, grey)


class AnsiParser:
    """Turns a stream with SGR escapes into (text, style) runs.

    Only colour and weight are understood; cursor movement, erasing and the
    rest are dropped, because the output view is append-only and a program that
    needs those needs a real terminal.
    """

    SGR = re.compile(r"\x1b\[([0-9;]*)m")
    OTHER = re.compile(r"\x1b\[[0-9;?]*[A-Za-z]|\x1b\][^\x07]*\x07|\x1b[()][AB0-2]")

    def __init__(self):
        self.pending = ""
        self.foreground = None
        self.bold = False

    def reset(self):
        self.foreground = None
        self.bold = False

    def style(self):
        return {"colour": self.foreground, "bold": self.bold}

    def feed(self, text):
        """Returns a list of (text, style) runs; incomplete escapes are kept."""
        text = (self.pending + text).replace("\r\n", "\n")
        self.pending = ""

        runs = []
        position = 0
        index = 0
        while True:
            match = self.SGR.search(text, position)
            other = self.OTHER.search(text, position)
            if other is not None and (match is None or other.start() < match.start()):
                if other.start() > position:
                    runs.append((text[position:other.start()], self.style()))
                position = other.end()
                continue
            if match is None:
                break
            if match.start() > position:
                runs.append((text[position:match.start()], self.style()))
            self._apply(match.group(1))
            position = match.end()
            index += 1

        remainder = text[position:]
        # An escape split across reads: hold it back until the rest arrives.
        tail = remainder.rfind("\x1b")
        if tail != -1 and len(remainder) - tail < 12 and "m" not in remainder[tail:]:
            self.pending = remainder[tail:]
            remainder = remainder[:tail]

        # A carriage return at the end of a line would otherwise show as a
        # stray character; progress bars redraw far faster than anyone reads.
        remainder = remainder.replace("\r", "\n")
        if remainder:
            runs.append((remainder, self.style()))
        return runs

    def _apply(self, parameters):
        codes = [int(part) if part else 0 for part in parameters.split(";")] or [0]
        index = 0
        while index < len(codes):
            code = codes[index]
            if code == 0:
                self.reset()
            elif code == 1:
                self.bold = True
            elif code == 22:
                self.bold = False
            elif 30 <= code <= 37:
                self.foreground = BASE_COLOURS[code - 30]
            elif code == 39:
                self.foreground = None
            elif 90 <= code <= 97:
                self.foreground = BASE_COLOURS[code - 90 + 8]
            elif code == 38 and index + 2 < len(codes) and codes[index + 1] == 5:
                self.foreground = colour_256(codes[index + 2])
                index += 2
            elif code == 38 and index + 4 < len(codes) and codes[index + 1] == 2:
                self.foreground = "#%02x%02x%02x" % (codes[index + 2], codes[index + 3],
                                                     codes[index + 4])
                index += 4
            index += 1


# Printed after every command so the panel can learn the working directory and
# the exit status without parsing the shell's own prompt.
MARKER = "\x1e"


class TerminalPanel(QWidget):
    """A shell in a dock, with a prompt line of its own."""

    def __init__(self, theme, root, parent=None):
        super().__init__(parent)
        self.theme = theme
        self.root = root
        self.cwd = root
        self.history = []
        self.history_index = 0
        self.parser = AnsiParser()
        self._buffer = ""

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(0)

        toolbar = QWidget()
        row = QHBoxLayout(toolbar)
        row.setContentsMargins(6, 4, 6, 4)
        self.status = QLabel(f"{os.path.basename(root)} — shell")
        self.clear_button = QPushButton("Clear")
        self.clear_button.setFixedHeight(22)
        self.clear_button.clicked.connect(self.clear)
        row.addWidget(self.status, 1)
        row.addWidget(self.clear_button)
        layout.addWidget(toolbar)

        self.output = QPlainTextEdit()
        self.output.setReadOnly(True)
        self.output.setFont(monospace(11))
        self.output.setLineWrapMode(QPlainTextEdit.NoWrap)
        # The output takes Ctrl+C too: clicking into the scrollback to read a
        # command's output is normal, and from there Ctrl+C has to reach the
        # running command, not just a read-only text view.
        self.output.installEventFilter(self)
        layout.addWidget(self.output, 1)

        prompt_row = QWidget()
        prompt_layout = QHBoxLayout(prompt_row)
        prompt_layout.setContentsMargins(6, 2, 6, 4)
        prompt_layout.setSpacing(6)

        self.prompt = QLabel("$")
        self.prompt.setFont(monospace(11))
        prompt_layout.addWidget(self.prompt)

        self.input = QLineEdit()
        self.input.setFont(monospace(11))
        self.input.setPlaceholderText("Type a command and press Enter")
        self.input.returnPressed.connect(self.submit)
        self.input.installEventFilter(self)
        prompt_layout.addWidget(self.input, 1)
        layout.addWidget(prompt_row)

        self.process = QProcess(self)
        self.process.setProcessChannelMode(QProcess.MergedChannels)
        self.process.readyReadStandardOutput.connect(self._read)
        self.process.finished.connect(self._shell_exited)

        self.apply_theme(theme)
        self.start()

    # ------------------------------------------------------------- lifecycle

    def start(self):
        shell, arguments = self._shell()
        self._is_cmd = os.path.basename(shell).lower().startswith("cmd")
        environment = QProcessEnvironment.systemEnvironment()
        environment.insert("TERM", "xterm-256color")
        environment.insert("CLICOLOR", "1")
        if not WINDOWS:
            environment.insert("TMPDIR", os.environ.get("TMPDIR", "/tmp"))
            # A prompt that never appears, so output is only what commands print.
            environment.insert("PS1", "")
            environment.insert("PS2", "")
        self.process.setProcessEnvironment(environment)
        self.process.setWorkingDirectory(self.cwd)

        # In its own session, so a signal sent to "the shell and its children"
        # cannot reach the IDE as well. `setsid` is util-linux; on Windows there
        # is no such thing and Ctrl+C restarts the shell instead (see interrupt).
        self._group = (not WINDOWS) and bool(shutil.which("setsid"))
        if self._group:
            self.process.start("setsid", [shell, *arguments])
        else:
            self.process.start(shell, arguments)

        if not self.process.waitForStarted(5000):
            self.write(f"could not start {shell}: {self.process.errorString()}\n", error=True)
            return False

        self.write(f"sh: {os.path.basename(shell)} in {self.cwd}\n", info=True)
        self._update_prompt()
        return True

    def _shell(self):
        """The shell to run, and its arguments.

        bash where there is one — Git Bash and WSL both provide it on Windows,
        and it is the only shell this panel's command dialect is written for.
        Failing that, `cmd.exe`, whose dialect differs in exactly two places:
        `%ERRORLEVEL%` and `%CD%` instead of `$?` and `$PWD`, and `echo` instead
        of `printf` for the marker.
        """
        bash = shutil.which("bash")
        if bash:
            return bash, ["--norc", "--noprofile"]
        if WINDOWS:
            return os.environ.get("COMSPEC", "cmd.exe"), ["/Q", "/K"]
        return shutil.which("sh") or "/bin/sh", []

    def stop(self):
        self._stopped_at = time.monotonic()
        if self.process.state() != QProcess.NotRunning:
            self.process.kill()
            self.process.waitForFinished(500)

    def restart(self):
        self.stop()
        self.start()

    def interrupt(self):
        """Ctrl+C: stop what is running, and leave a usable shell behind.

        The shell reads commands from a pipe, so it dies with the command it
        is running — there is no terminal to catch SIGINT and return to a
        prompt. The command is stopped with SIGINT (programs get a chance to
        clean up), then the shell is replaced by a fresh one in the same
        directory. Shell variables do not survive; the history and the working
        directory do.
        """
        self.write("^C\n", info=True)
        self._stopped_at = time.monotonic()
        if self.process.state() == QProcess.NotRunning:
            self.restart()
            return

        pid = self.process.processId()
        if pid and WINDOWS:
            # No SIGINT to send to a process group here. `taskkill /T` ends the
            # shell and whatever it started, which is the only way to abandon a
            # long command without leaving an orphan behind.
            subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"],
                           capture_output=True)
            self.process.waitForFinished(1500)
            self.restart()
            return

        if pid:
            self._signal(pid, 2)                    # SIGINT
            if self.process.waitForFinished(400):
                self.restart()
                return
            self._signal(pid, 15)                   # SIGTERM: the command ignored it
            if self.process.waitForFinished(400):
                self.restart()
                return

        self.process.kill()
        self.process.waitForFinished(400)
        self.restart()

    def _signal(self, pid, number):
        """Sends a signal to the shell's own process group, or to the shell."""
        try:
            if self._group:
                os.killpg(pid, number)
            else:
                os.kill(pid, number)
        except (OSError, AttributeError):
            pass

    # ------------------------------------------------------------------ input

    def eventFilter(self, watched, event):
        from PySide6.QtCore import QEvent

        if watched is self.output and event.type() == QEvent.KeyPress:
            # Copy still wins when something is selected: that is the other
            # thing Ctrl+C does in a text view.
            if (event.key() == Qt.Key_C and event.modifiers() & Qt.ControlModifier
                    and not self.output.textCursor().hasSelection()):
                self.interrupt()
                return True
            return super().eventFilter(watched, event)

        # `input` is created after `output`, and events reach the filter during
        # layout, so this cannot assume it is there yet.
        if watched is getattr(self, "input", None) and event.type() == QEvent.KeyPress:
            key = event.key()
            modifiers = event.modifiers()

            if key == Qt.Key_C and modifiers & Qt.ControlModifier:
                self.interrupt()
                return True
            if key == Qt.Key_L and modifiers & Qt.ControlModifier:
                self.clear()
                return True
            if key == Qt.Key_Up:
                self._history(-1)
                return True
            if key == Qt.Key_Down:
                self._history(1)
                return True

        return super().eventFilter(watched, event)

    def _history(self, direction):
        if not self.history:
            return
        self.history_index = max(0, min(len(self.history), self.history_index + direction))
        if self.history_index == len(self.history):
            self.input.clear()
        else:
            self.input.setText(self.history[self.history_index])
            self.input.setCursorPosition(len(self.input.text()))

    def submit(self):
        """Runs what was typed."""
        command = self.input.text().strip()
        self.input.clear()
        if not command:
            return

        if command in ("clear", "cls"):
            self.clear()
            return

        self.history.append(command)
        self.history_index = len(self.history)
        self.write(f"$ {command}\n", prompt=True)

        if self.process.state() == QProcess.NotRunning:
            self.restart()

        # The command, then a marker line carrying its status and the new
        # working directory. `printf` rather than `echo` so nothing is quoted
        # wrongly, and `;` rather than `&&` so a failure still reports.
        self.process.write(self._command_script(command).encode("utf-8"))

    # ----------------------------------------------------------------- output

    # What bash says when there is no terminal to attach to. True, and of no
    # interest to the person using the window.
    _STARTUP_NOISE = (
        "bash: cannot set terminal process group",
        "bash: no job control in this shell",
        "bash: tcsetattr: Inappropriate ioctl",
    )

    def _command_script(self, command):
        """A command, then a marker line carrying its status and directory.

        Two dialects. bash: `$?` and `$PWD` after the command, with `printf`
        so nothing is quoted wrongly and `;`-style reporting so a failure still
        arrives. cmd.exe has no `printf` and no such variables — `%ERRORLEVEL%`
        and `%CD%` are expanded when the line is read, which is after the
        command has run, so the same trick works with `echo`.
        """
        if self._is_cmd:
            return (f"{command}\r\n"
                    f"echo {MARKER}%ERRORLEVEL%{MARKER}%CD%{MARKER}\r\n")
        return (f"{command}\n"
                f"__tern_status=$?\n"
                f"printf '{MARKER}%s{MARKER}%s{MARKER}\\n' \"$__tern_status\" \"$PWD\"\n")

    def _read(self):
        data = bytes(self.process.readAllStandardOutput()).decode("utf-8", "replace")
        self._buffer += data
        for prefix in self._STARTUP_NOISE:
            self._buffer = re.sub(r"(?m)^" + re.escape(prefix) + r".*\n", "", self._buffer)

        # Text before the marker is output; the marker line itself is metadata.
        while MARKER in self._buffer:
            before, _, rest = self._buffer.partition(MARKER)
            if before:
                self._append(before)
            status, _, rest = rest.partition(MARKER)
            directory, separator, remainder = rest.partition(MARKER)
            if not separator:
                # The marker arrived without its second half yet.
                self._buffer = MARKER + status + MARKER + directory
                return
            self._buffer = remainder.lstrip("\n")
            self._reported(status.strip(), directory.strip())

    def _reported(self, status, directory):
        if directory and os.path.isdir(directory) and directory != self.cwd:
            self.cwd = directory
            self._update_prompt()
            if self.process.state() != QProcess.NotRunning:
                self.process.setWorkingDirectory(self.cwd)
        if status not in ("", "0"):
            self.status.setText(f"exit {status} — {os.path.basename(self.cwd) or self.cwd}")
        else:
            self.status.setText(f"{os.path.basename(self.cwd) or self.cwd} — shell")

    def _append(self, text):
        cursor = self.output.textCursor()
        cursor.movePosition(QTextCursor.End)
        for run, style in self.parser.feed(text):
            if not run:
                continue
            fmt = QTextCharFormat()
            if style["colour"]:
                fmt.setForeground(QColor(style["colour"]))
            if style["bold"]:
                fmt.setFontWeight(700)
            cursor.insertText(run, fmt)
        self.output.setTextCursor(cursor)
        self.output.ensureCursorVisible()

    def write(self, text, error=False, info=False, prompt=False):
        cursor = self.output.textCursor()
        cursor.movePosition(QTextCursor.End)
        fmt = QTextCharFormat()
        if error:
            fmt.setForeground(QColor(self.theme.get("error", "#ff6b68")))
        elif info:
            fmt.setForeground(QColor(self.theme.get("fg_dim", "#808080")))
        elif prompt:
            fmt.setForeground(QColor(self.theme.get("accent", "#3592c4")))
            fmt.setFontWeight(600)
        cursor.insertText(text, fmt)
        self.output.setTextCursor(cursor)
        self.output.ensureCursorVisible()

    def clear(self):
        self.output.clear()
        self.parser.reset()

    def _update_prompt(self):
        home = os.path.expanduser("~")
        shown = self.cwd
        if home and self.cwd.startswith(home):
            shown = "~" + self.cwd[len(home):]
        self.prompt.setText(shown + " $")

    def _shell_exited(self, code, _status):
        # A shell we have just stopped or interrupted is not news; the notice
        # arrives a moment after we asked for it.
        if time.monotonic() - getattr(self, "_stopped_at", 0.0) < 2.0:
            return
        self.write(f"\n[shell exited with {code}]\n", info=True)
        self.status.setText("shell exited")

    # ------------------------------------------------------------- appearance

    def apply_theme(self, theme):
        self.theme = theme
        self.output.setStyleSheet(
            f"QPlainTextEdit {{ background: {theme['bg']}; color: {theme['fg']}; border: none;"
            f" selection-background-color: {theme['selection']}; }}")
        self.input.setStyleSheet(
            f"QLineEdit {{ background: {theme['bg']}; color: {theme['fg']};"
            f" border: none; padding: 2px 0; }}")
        self.prompt.setStyleSheet(f"color: {theme['accent']};")
        self.status.setStyleSheet(f"color: {theme['fg_dim']};")
        self.setStyleSheet(f"QWidget {{ background: {theme['panel']}; }}")

    def closeEvent(self, event):
        self.stop()
        super().closeEvent(event)
