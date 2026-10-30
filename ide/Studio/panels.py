"""The tool windows: project, structure, problems, and the run console.

Each is a small widget with a model behind it, so the main window only wires
signals and never reaches into a list of items.
"""

import os

from PySide6.QtCore import QDir, QModelIndex, Qt, Signal
from PySide6.QtGui import QColor, QTextCharFormat, QTextCursor
from PySide6.QtWidgets import (QAbstractItemView, QFileSystemModel, QHBoxLayout,
                               QLabel, QLineEdit, QPlainTextEdit, QPushButton,
                               QTreeView, QTreeWidget, QTreeWidgetItem,
                               QVBoxLayout, QWidget)

from .theme import colour, monospace
from .vcs import Repository


VCS_LETTERS = {
    "M": "modified", "A": "added", "D": "deleted", "R": "renamed",
    "?": "untracked", "U": "conflicted", "C": "copied",
}

KIND_ICONS = {
    "function": "ƒ",
    "struct": "◈",
    "variable": "▪",
    "constant": "◆",
    "field": "·",
    "method": "ƒ",
    "parameter": "▪",
    "import": "→",
}


class ProjectPanel(QWidget):
    """The file tree, filtered to what a TERN project contains."""

    openRequested = Signal(str)

    def __init__(self, root, theme, parent=None):
        super().__init__(parent)
        self.root = root
        self.theme = theme

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(0)

        header = QWidget()
        header_layout = QHBoxLayout(header)
        header_layout.setContentsMargins(6, 4, 6, 4)
        header_layout.setSpacing(4)
        self.filter = QLineEdit()
        self.filter.setPlaceholderText("Filter files")
        self.filter.textChanged.connect(self._apply_filter)
        header_layout.addWidget(self.filter)
        layout.addWidget(header)

        self.model = QFileSystemModel(self)
        self.model.setRootPath(root)
        self.model.setFilter(QDir.AllDirs | QDir.Files | QDir.NoDotAndDotDot)
        self.model.setNameFilters(["*.tern", "*.md", "*.txt", "*.json"])
        self.model.setNameFilterDisables(False)

        self.tree = QTreeView()
        self.tree.setModel(self.model)
        self.tree.setRootIndex(self.model.index(root))
        self.tree.setHeaderHidden(True)
        self.tree.setAnimated(False)
        self.tree.setIndentation(14)
        self.tree.setSortingEnabled(True)
        self.tree.sortByColumn(0, Qt.AscendingOrder)
        for column in range(1, 4):
            self.tree.setColumnHidden(column, True)
        self.tree.doubleClicked.connect(self._activate)
        layout.addWidget(self.tree, 1)
        self.apply_theme(theme)

    def apply_theme(self, theme):
        self.theme = theme
        self.tree.setStyleSheet(
            f"QTreeView {{ background: {theme['panel']}; color: {theme['fg']}; }}")

    def _apply_filter(self, text):
        self.model.setNameFilters(
            [f"*{text}*"] if text else ["*.tern", "*.md", "*.txt", "*.json"])

    def _activate(self, index: QModelIndex):
        path = self.model.filePath(index)
        if os.path.isfile(path):
            self.openRequested.emit(path)

    def select(self, path):
        index = self.model.index(path)
        if index.isValid():
            self.tree.setCurrentIndex(index)
            self.tree.scrollTo(index)


class StructurePanel(QWidget):
    """The outline of the active file: everything it declares, in order."""

    navigate = Signal(int)      # character offset

    def __init__(self, theme, parent=None):
        super().__init__(parent)
        self.theme = theme
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)

        self.tree = QTreeWidget()
        self.tree.setHeaderLabels(["Name", "Detail"])
        self.tree.setColumnWidth(0, 150)
        self.tree.setRootIsDecorated(True)
        self.tree.setIndentation(12)
        self.tree.itemActivated.connect(self._activate)
        self.tree.itemClicked.connect(self._activate)
        layout.addWidget(self.tree)

        self.empty = QLabel("No declarations in this file.")
        self.empty.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.empty)
        self.empty.hide()
        self.apply_theme(theme)

    def apply_theme(self, theme):
        self.theme = theme
        self.tree.setStyleSheet(
            f"QTreeWidget {{ background: {theme['panel']}; color: {theme['fg']}; border: none; }}"
            f"QTreeWidget::item:selected {{ background: {theme['accent']}; color: #ffffff; }}")
        self.empty.setStyleSheet(f"color: {theme['fg_dim']};")

    def set_symbols(self, symbols, offset_for_line):
        self.tree.clear()
        # Parameters and loop variables are not shown: the outline is for
        # navigating the file's declarations, as Rider's Structure view is.
        symbols = [s for s in symbols if s.get("kind") != 13]
        if not symbols:
            self.empty.show()
            self.tree.hide()
            return

        self.empty.hide()
        self.tree.show()

        for symbol in symbols:
            line = symbol.get("range", {}).get("start", {}).get("line", 0)
            name = symbol.get("name", "")
            detail = symbol.get("detail", "") or ""

            # The outline marks what a declaration is, the way Rider's does.
            glyph = {
                12: "\u0192",   # function
                6: "\u0192",    # method
                23: "\u25c8",   # struct
                14: "\u25c6",   # constant
                2: "\u2192",    # import
            }.get(symbol.get("kind", 13), "\u25aa")

            item = QTreeWidgetItem([f"{glyph}  {name}", detail])
            item.setData(0, Qt.UserRole, offset_for_line(line))
            self.tree.addTopLevelItem(item)

    def _activate(self, item, _column=0):
        offset = item.data(0, Qt.UserRole)
        if offset is not None:
            self.navigate.emit(int(offset))


class ProblemsPanel(QWidget):
    """Every diagnostic in the project, clickable."""

    navigate = Signal(str, int, int)

    def __init__(self, theme, parent=None):
        super().__init__(parent)
        self.theme = theme
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)

        self.tree = QTreeWidget()
        self.tree.setHeaderLabels(["Description", "File", "Line"])
        self.tree.setColumnWidth(0, 420)
        self.tree.setRootIsDecorated(False)
        self.tree.setAlternatingRowColors(True)
        self.tree.itemActivated.connect(self._activate)
        self.tree.itemClicked.connect(self._activate)
        layout.addWidget(self.tree)

        self.summary = QLabel("No problems")
        layout.addWidget(self.summary)
        self.apply_theme(theme)

    def apply_theme(self, theme):
        self.theme = theme
        self.tree.setStyleSheet(
            f"QTreeWidget {{ background: {theme['panel']}; color: {theme['fg']}; border: none; }}"
            f"QTreeWidget::item:selected {{ background: {theme['accent']}; color: #ffffff; }}")
        self.summary.setStyleSheet(f"color: {theme['fg_dim']}; padding: 4px 8px;")

    def set_problems(self, entries):
        """entries: [(severity, file, line0, character, message, code)]"""
        self.tree.clear()
        for severity, path, line, character, message, code in entries:
            label = ("Error" if severity == 1 else "Warning") + f": {message}  [{code}]"
            item = QTreeWidgetItem([label, os.path.basename(path), str(line + 1)])
            item.setForeground(0, QColor(self.theme["error"] if severity == 1 else self.theme["warning"]))
            item.setData(0, Qt.UserRole, (path, line, character))
            self.tree.addTopLevelItem(item)

        errors = sum(1 for entry in entries if entry[0] == 1)
        warnings = len(entries) - errors
        self.summary.setText(
            f"{errors} error(s), {warnings} warning(s)" if entries else "No problems")

    def _activate(self, item, _column=0):
        data = item.data(0, Qt.UserRole)
        if data:
            path, line, character = data
            self.navigate.emit(path, line, character)


class ConsolePanel(QWidget):
    """The Run tool window: program output, coloured by stream."""

    def __init__(self, theme, parent=None):
        super().__init__(parent)
        self.theme = theme
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(0)

        toolbar = QWidget()
        row = QHBoxLayout(toolbar)
        row.setContentsMargins(6, 4, 6, 4)
        self.status = QLabel("No process running")
        self.status.setStyleSheet(f"color: {theme['fg_dim']};")
        self.clear_button = QPushButton("Clear")
        self.clear_button.setFixedHeight(22)
        self.clear_button.clicked.connect(self.clear)
        row.addWidget(self.status, 1)
        row.addWidget(self.clear_button)
        layout.addWidget(toolbar)

        self.output = QPlainTextEdit()
        self.output.setReadOnly(True)
        self.output.setFont(monospace(11))
        layout.addWidget(self.output, 1)

        self._error_format = QTextCharFormat()
        self._info_format = QTextCharFormat()
        self.apply_theme(theme)

    def apply_theme(self, theme):
        self.theme = theme
        self.output.setStyleSheet(
            f"background: {theme['bg']}; color: {theme['fg']}; border-top: 1px solid {theme['border']};"
            f" selection-background-color: {theme['selection']};")
        self.status.setStyleSheet(f"color: {theme['fg_dim']};")
        self._error_format.setForeground(QColor(theme["error"]))
        self._info_format.setForeground(QColor(theme["fg_dim"]))

    def clear(self):
        self.output.clear()

    def write(self, text, error=False, info=False):
        cursor = self.output.textCursor()
        cursor.movePosition(QTextCursor.End)
        fmt = self._error_format if error else self._info_format if info else QTextCharFormat()
        if error or info:
            cursor.insertText(text, fmt)
        else:
            cursor.insertText(text)
        self.output.setTextCursor(cursor)
        self.output.ensureCursorVisible()

    def set_status(self, text):
        self.status.setText(text)


class TerminalPanel(ConsolePanel):
    """A second console for shell commands, as Rider has."""


class SearchEverywhereDialog(QWidget):
    """Ctrl+Shift+A: symbols across the project, as a popup list."""

    chosen = Signal(str, int, int)     # path, line, character

    def __init__(self, theme, parent=None):
        super().__init__(parent, Qt.Popup)
        self.theme = theme
        layout = QVBoxLayout(self)
        layout.setContentsMargins(8, 8, 8, 8)
        layout.setSpacing(6)

        self.input = QLineEdit()
        self.input.setPlaceholderText("Search classes, functions and files")
        self.input.textChanged.connect(self._on_text)
        self.input.returnPressed.connect(self._accept)
        layout.addWidget(self.input)

        self.results = QTreeWidget()
        self.results.setHeaderHidden(True)
        self.results.setRootIsDecorated(False)
        self.results.itemActivated.connect(lambda *_: self._accept())
        self.results.setStyleSheet(
            f"QTreeWidget {{ background: {theme['panel']}; color: {theme['fg']}; border: none; }}"
            f"QTreeWidget::item:selected {{ background: {theme['accent']}; color: #ffffff; }}")
        layout.addWidget(self.results, 1)

        self.resize(560, 380)
        self._entries = []

    def search(self, text):
        self.input.setText(text)
        self.input.setFocus()

    def show_results(self, entries):
        """entries: [(label, detail, path, line, character)]"""
        self._entries = entries
        self.results.clear()
        for label, detail, path, line, character in entries:
            item = QTreeWidgetItem([f"{label}    {detail}" if detail else label])
            item.setData(0, Qt.UserRole, (path, line, character))
            self.results.addTopLevelItem(item)
        if self.results.topLevelItemCount():
            self.results.setCurrentItem(self.results.topLevelItem(0))

    def _on_text(self):
        self.textEdited.emit(self.input.text())

    textEdited = Signal(str)

    def _accept(self):
        item = self.results.currentItem()
        if item is None:
            return
        path, line, character = item.data(0, Qt.UserRole)
        self.chosen.emit(path, line, character)
        self.hide()


class ChangesPanel(QWidget):
    """The Commit tool window: what has changed, and the box to commit it.

    Rows follow Rider: a letter and a colour per status, the path, and the
    directory in grey. Staging is a checkbox rather than a two-list view, which
    is fewer clicks and one less thing to get out of step with git.
    """

    openRequested = Signal(str)
    diffRequested = Signal(str, bool)          # path, staged
    committed = Signal(str)                    # the message that landed
    refreshed = Signal()

    def __init__(self, root, theme, parent=None):
        super().__init__(parent)
        self.theme = theme
        self.repository = Repository.open(root)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(0)

        self.header = QLabel("")
        self.header.setObjectName("changesHeader")
        layout.addWidget(self.header)

        self.tree = QTreeWidget()
        # Staged is a checkbox of its own, next to the status letter, so a
        # narrow column cannot hide one behind the other.
        self.tree.setHeaderLabels(["", "", "Name", "Directory"])
        self.tree.setRootIsDecorated(False)
        self.tree.setAlternatingRowColors(False)
        self.tree.setSelectionMode(QAbstractItemView.ExtendedSelection)
        self.tree.setColumnWidth(0, 22)
        self.tree.setColumnWidth(1, 22)
        self.tree.setColumnWidth(2, 180)
        self.tree.itemDoubleClicked.connect(self._on_double_click)
        self.tree.itemChanged.connect(self._on_check)
        layout.addWidget(self.tree, 1)

        message_row = QWidget()
        message_layout = QVBoxLayout(message_row)
        message_layout.setContentsMargins(6, 4, 6, 6)
        message_layout.setSpacing(4)

        self.message = QPlainTextEdit()
        self.message.setPlaceholderText("Commit message")
        self.message.setFixedHeight(46)
        self.message.setFont(monospace(11))
        message_layout.addWidget(self.message)

        buttons = QHBoxLayout()
        buttons.setSpacing(6)
        self.refresh_button = QPushButton("Refresh")
        self.refresh_button.clicked.connect(self.refresh)
        self.stage_all_button = QPushButton("Stage all")
        self.stage_all_button.clicked.connect(self._stage_all)
        self.commit_button = QPushButton("Commit")
        self.commit_button.setDefault(True)
        self.commit_button.clicked.connect(self.commit)
        buttons.addWidget(self.refresh_button)
        buttons.addWidget(self.stage_all_button)
        buttons.addStretch(1)
        buttons.addWidget(self.commit_button)
        message_layout.addLayout(buttons)

        layout.addWidget(message_row)
        self.apply_theme(theme)
        self.refresh()

    # ------------------------------------------------------------- appearance

    def apply_theme(self, theme):
        self.theme = theme
        self.tree.setStyleSheet(
            f"QTreeWidget {{ background: {theme['panel']}; color: {theme['fg']}; border: none; }}"
            f"QTreeWidget::item:selected {{ background: {theme['accent']}; color: #ffffff; }}"
            "QHeaderView::section { background: " + theme["panel"] + "; color: "
            + theme["fg_dim"] + "; border: none; padding: 3px; }")
        self.header.setStyleSheet(
            f"color: {theme['fg_dim']}; padding: 4px 8px;"
            f" border-bottom: 1px solid {theme['border']};")
        self.message.setStyleSheet(
            f"background: {theme['bg']}; color: {theme['fg']};"
            f" border: 1px solid {theme['border']};")
        self._refresh_colours()

    def _status_colour(self, letter):
        if letter == "?":
            return self.theme.get("fg_dim", "#888888")
        if letter in ("D", "U"):
            return self.theme.get("error", "#cc4444")
        return self.theme.get("accent", "#4a88c7")

    def _refresh_colours(self):
        for index in range(self.tree.topLevelItemCount()):
            item = self.tree.topLevelItem(index)
            letter = item.data(0, Qt.UserRole + 1) or ""
            item.setForeground(1, QColor(self._status_colour(letter)))

    # ---------------------------------------------------------------- content

    def refresh(self):
        """Re-reads git. Cheap enough to call on every save and window focus."""
        self.tree.blockSignals(True)
        self.tree.clear()

        if self.repository is None:
            self.header.setText("Not a git repository")
            self.tree.blockSignals(False)
            self._set_buttons_enabled(False)
            self.refreshed.emit()
            return

        branch = self.repository.branch()
        ahead_behind = self.repository.upstream_ahead_behind()
        suffix = ""
        if ahead_behind is not None:
            ahead, behind = ahead_behind
            if ahead:
                suffix += f"  ↑{ahead}"
            if behind:
                suffix += f"  ↓{behind}"
        self.header.setText(f"{branch}{suffix}")

        files = self.repository.status()
        for changed in files:
            item = QTreeWidgetItem([
                "", changed.letter, os.path.basename(changed.path),
                os.path.dirname(changed.path)])
            item.setData(0, Qt.UserRole, changed.path)
            item.setData(0, Qt.UserRole + 1, changed.letter)
            item.setData(0, Qt.UserRole + 2, changed.staged)
            item.setToolTip(2, changed.path)
            item.setToolTip(1, changed.kind)
            flags = Qt.ItemIsEnabled | Qt.ItemIsSelectable
            if not changed.untracked:
                flags |= Qt.ItemIsUserCheckable
                item.setCheckState(0, Qt.Checked if changed.staged else Qt.Unchecked)
            item.setFlags(flags)
            item.setForeground(1, QColor(self._status_colour(changed.letter)))
            item.setForeground(2, QColor(self.theme["fg"]))
            item.setForeground(3, QColor(self.theme["fg_dim"]))
            self.tree.addTopLevelItem(item)

        if not files:
            self.header.setText(f"{branch}{suffix}  —  no changes")
        self._refresh_colours()
        self._set_buttons_enabled(bool(files))
        self.tree.blockSignals(False)
        self.refreshed.emit()

    def _set_buttons_enabled(self, enabled):
        self.commit_button.setEnabled(enabled and self.repository is not None)
        self.stage_all_button.setEnabled(enabled and self.repository is not None)

    def selected_paths(self):
        return [item.data(0, Qt.UserRole) for item in self.tree.selectedItems()
                if item.data(0, Qt.UserRole)]

    # ---------------------------------------------------------------- actions

    def _on_double_click(self, item, _column):
        path = item.data(0, Qt.UserRole)
        if not path:
            return
        staged = bool(item.data(0, Qt.UserRole + 2))
        full = os.path.join(self.repository.root, path) if self.repository else path
        if os.path.exists(full):
            self.openRequested.emit(full)
        self.diffRequested.emit(full, staged)

    def _on_check(self, item, column):
        if column != 0 or self.repository is None:
            return
        path = item.data(0, Qt.UserRole)
        if not path:
            return
        self.tree.blockSignals(True)
        if item.checkState(0) == Qt.Checked:
            self.repository.stage([path])
        else:
            self.repository.unstage([path])
        self.tree.blockSignals(False)
        self.refresh()

    def _stage_all(self):
        if self.repository is None:
            return
        self.repository.stage([changed.path for changed in self.repository.status()])
        self.refresh()

    def commit(self):
        if self.repository is None:
            return False, "not a repository"

        message = self.message.toPlainText().strip()
        if not message:
            self.header.setText(f"{self.repository.branch()}  —  write a message first")
            return False, "no message"

        staged = [changed for changed in self.repository.status() if changed.staged]
        if not staged:
            self._stage_all()
            staged = [changed for changed in self.repository.status() if changed.staged]
        if not staged:
            return False, "nothing staged"

        ok, output = self.repository.commit(message)
        if ok:
            self.message.clear()
            self.refresh()
            self.committed.emit(message)
        else:
            self.header.setText(f"{self.repository.branch()}  —  commit failed")
        return ok, output


class DiffPanel(QWidget):
    """A read-only view of one file's diff, coloured by line kind."""

    def __init__(self, theme, parent=None):
        super().__init__(parent)
        self.theme = theme

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(0)

        self.banner = QLabel("")
        layout.addWidget(self.banner)

        self.view = QPlainTextEdit()
        self.view.setReadOnly(True)
        self.view.setFont(monospace(11))
        self.view.setLineWrapMode(QPlainTextEdit.NoWrap)
        layout.addWidget(self.view, 1)
        self.apply_theme(theme)

    def apply_theme(self, theme):
        self.theme = theme
        self.banner.setStyleSheet(
            f"color: {theme['fg_dim']}; padding: 3px 8px;"
            f" border-bottom: 1px solid {theme['border']}; background: {theme['panel']};")
        self.view.setStyleSheet(
            f"QPlainTextEdit {{ background: {theme['bg']}; color: {theme['fg']}; border: none;"
            f" selection-background-color: {theme['selection']}; }}")
        self._recolour()

    def set_diff(self, title, lines):
        """`lines` is what vcs.parse_diff returns: (kind, text) pairs."""
        from PySide6.QtGui import QTextCharFormat

        self.banner.setText(title)
        self.lines = lines
        self.view.setPlainText("\n".join(text for _kind, text in lines))
        self._recolour()

    def _recolour(self):
        from PySide6.QtGui import QTextCharFormat

        if not hasattr(self, "lines"):
            return

        colours = {
            "add": self.theme.get("added", "#4a8a4a"),
            "remove": self.theme.get("error", "#cc4444"),
            "hunk": self.theme.get("accent", "#4a88c7"),
            "meta": self.theme.get("fg_dim", "#888888"),
        }

        document = self.view.document()
        block = document.begin()
        for kind, text in self.lines:
            if block is None or not block.isValid():
                break
            colour = colours.get(kind)
            if colour is not None:
                cursor = QTextCursor(block)
                cursor.select(QTextCursor.BlockUnderCursor)
                fmt = QTextCharFormat()
                fmt.setForeground(QColor(colour))
                cursor.mergeCharFormat(fmt)
            block = block.next()
        del document
