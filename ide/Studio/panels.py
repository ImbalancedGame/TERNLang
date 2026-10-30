"""The tool windows: project, structure, problems, and the run console.

Each is a small widget with a model behind it, so the main window only wires
signals and never reaches into a list of items.
"""

import os

from PySide6.QtCore import QDir, QModelIndex, Qt, Signal
from PySide6.QtGui import QColor, QFont, QTextCharFormat, QTextCursor
from PySide6.QtWidgets import (QAbstractItemView, QComboBox, QFileSystemModel, QHBoxLayout, QLabel,
                               QLineEdit, QPlainTextEdit, QPushButton, QStackedWidget,
                               QTabWidget, QTreeView, QTreeWidget, QTreeWidgetItem,
                               QVBoxLayout, QWidget)

from .theme import colour, monospace


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
