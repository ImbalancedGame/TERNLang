"""The code editor: a QPlainTextEdit with the trappings Rider's editor has.

Line numbers, the current-line highlight, indent guides, bracket matching,
error and warning underlines with hover text, completion popup, hover
documentation, and the multi-caret-free but keyboard-complete editing a
scripting IDE needs.
"""

import re

from PySide6.QtCore import QPoint, QRect, QSize, Qt, Signal
from PySide6.QtGui import (QColor, QFontMetrics, QKeyEvent, QPainter, QPen,
                           QTextCharFormat, QTextCursor, QTextFormat)
from PySide6.QtWidgets import (QLabel, QListWidget, QListWidgetItem,
                               QPlainTextEdit, QTextEdit, QToolTip,
                               QVBoxLayout, QWidget)

from .highlighter import TernHighlighter
from .theme import colour, monospace


class LineNumberArea(QWidget):
    """The gutter. It delegates its painting to the editor, which knows the layout."""

    def __init__(self, editor):
        super().__init__(editor)
        self.editor = editor
        self.setCursor(Qt.ArrowCursor)

    def sizeHint(self):
        return QSize(self.editor.gutter_width(), 0)

    def paintEvent(self, event):
        self.editor.paint_gutter(event)


class ErrorStrip(QWidget):
    """The right-hand strip Rider shows: a mark per problem, at its line."""

    def __init__(self, editor):
        super().__init__(editor)
        self.editor = editor
        self.setFixedWidth(10)
        self.setMouseTracking(True)

    def paintEvent(self, event):
        theme = self.editor.theme
        painter = QPainter(self)
        painter.fillRect(self.rect(), colour(theme, "panel"))
        for severity, line, _message in self.editor.problems:
            block = self.editor.document().findBlockByNumber(line)
            if not block.isValid():
                continue
            y = self.editor.blockBoundingGeometry(block).translated(self.editor.contentOffset()).top()
            y += self.editor.viewportMargins().top()
            y -= self.editor.verticalScrollBar().value()
            if 0 <= y <= self.height():
                painter.fillRect(QRect(2, int(y) + 1, 6, 3),
                                 QColor(theme["error"] if severity == 1 else theme["warning"]))
        painter.end()

    def mouseMoveEvent(self, event):
        for severity, line, message in self.editor.problems:
            block = self.editor.document().findBlockByNumber(line)
            if not block.isValid():
                continue
            y = self.editor.blockBoundingGeometry(block).translated(self.editor.contentOffset()).top()
            y += self.editor.viewportMargins().top() - self.editor.verticalScrollBar().value()
            if y - 2 <= event.position().y() <= y + 4:
                QToolTip.showText(event.globalPosition().toPoint(), message, self)
                return
        QToolTip.hideText()


class CompletionPopup(QListWidget):
    """A completion list anchored under the caret, driven from the keyboard."""

    accepted = Signal(str)

    def __init__(self, parent):
        super().__init__(parent)
        self.setWindowFlags(Qt.Popup)
        self.setUniformItemSizes(True)
        self.setFocusPolicy(Qt.NoFocus)
        self.setMouseTracking(True)
        self.itemClicked.connect(self._choose)
        self._documentation = {}

    def show_items(self, items, theme, prefix):
        self.clear()
        self._documentation.clear()
        for item in items:
            label = item.get("label", "")
            detail = item.get("detail", "")
            text = f"{label}   {detail}" if detail else label
            entry = QListWidgetItem(text)
            documentation = item.get("documentation")
            if isinstance(documentation, dict):
                documentation = documentation.get("value")
            self._documentation[label] = documentation
            entry.setData(Qt.UserRole, label)
            self.addItem(entry)

        if self.count() == 0:
            self.hide()
            return

        # Narrow to what has been typed, and preselect the exact match.
        best = 0
        for row in range(self.count()):
            if self.item(row).data(Qt.UserRole) == prefix:
                best = row
                break

        self.setCurrentRow(best)
        self.setStyleSheet(f"""
            QListWidget {{ background: {theme['panel']}; border: 1px solid {theme['border']};
                           color: {theme['fg']}; outline: none; }}
            QListWidget::item {{ padding: 3px 8px; }}
            QListWidget::item:selected {{ background: {theme['accent']}; color: #ffffff; }}
        """)
        self.resize(420, min(280, 20 * self.count() + 6))
        self.show()

    def _choose(self, item):
        self.accepted.emit(item.data(Qt.UserRole))
        self.hide()

    def move_selection(self, delta):
        row = (self.currentRow() + delta) % max(1, self.count())
        self.setCurrentRow(row)

    def current_label(self):
        item = self.currentItem()
        return item.data(Qt.UserRole) if item else None

    def documentation_for(self, label):
        return self._documentation.get(label)


class Editor(QPlainTextEdit):
    """One open file."""

    cursorMoved = Signal(int, int)
    modificationChanged = Signal()
    saveRequested = Signal()
    runRequested = Signal()
    debugRequested = Signal()
    formatRequested = Signal()
    renameRequested = Signal()
    referencesRequested = Signal()
    definitionRequested = Signal()
    completionRequested = Signal(int)
    hoverRequested = Signal(int, int, QPoint)

    def __init__(self, path, text, theme, parent=None):
        super().__init__(parent)
        self.path = path
        self.theme = theme
        self.problems = []          # (severity, line, message)
        self._completion_items = []
        self._completion_start = 0
        self._brackets = (None, None)

        font = monospace(12)
        self.setFont(font)
        self.setTabStopDistance(QFontMetrics(font).horizontalAdvance(" ") * 4)
        self.setLineWrapMode(QPlainTextEdit.NoWrap)
        self.setCursorWidth(2)

        self.highlighter = TernHighlighter(self.document(), theme)
        self.setPlainText(text)

        self.gutter = LineNumberArea(self)
        self.strip = ErrorStrip(self)
        self.popup = CompletionPopup(self)

        self.blockCountChanged.connect(self._update_margins)
        self.updateRequest.connect(self._update_gutter)
        self.cursorPositionChanged.connect(self._on_cursor)
        self.textChanged.connect(self._on_text)

        self._update_margins()
        self._on_cursor()
        self.setFocusPolicy(Qt.StrongFocus)

    # ------------------------------------------------------------- appearance

    def apply_theme(self, theme):
        self.theme = theme
        self.highlighter.set_theme(theme)
        self._on_cursor()
        self.strip.update()

    def gutter_width(self):
        digits = max(3, len(str(max(1, self.blockCount()))))
        return 16 + QFontMetrics(self.font()).horizontalAdvance("9") * digits

    def _update_margins(self):
        self.setViewportMargins(self.gutter_width(), 0, 10, 0)

    def _update_gutter(self, rect, dy):
        if dy:
            self.gutter.scroll(0, dy)
            self.strip.scroll(0, dy)
        else:
            self.gutter.update(0, rect.y(), self.gutter.width(), rect.height())
            self.strip.update()

        if rect.contains(self.viewport().rect()):
            self._update_margins()

    def resizeEvent(self, event):
        super().resizeEvent(event)
        box = self.contentsRect()
        self.gutter.setGeometry(QRect(box.left(), box.top(), self.gutter_width(), box.height()))
        self.strip.setGeometry(QRect(box.right() - 10, box.top(), 10, box.height()))
        self._place_popup()

    def paint_gutter(self, event):
        painter = QPainter(self.gutter)
        painter.fillRect(event.rect(), colour(self.theme, "panel"))

        block = self.firstVisibleBlock()
        number = block.blockNumber()
        top = self.blockBoundingGeometry(block).translated(self.contentOffset()).top()
        bottom = top + self.blockBoundingRect(block).height()

        current = self.textCursor().blockNumber()
        error_lines = {line for severity, line, _ in self.problems if severity == 1}
        warning_lines = {line for severity, line, _ in self.problems if severity == 2}

        while block.isValid() and top <= event.rect().bottom():
            if block.isVisible() and bottom >= event.rect().top():
                line = number
                if line in error_lines:
                    painter.setPen(QPen(colour(self.theme, "error")))
                elif line in warning_lines:
                    painter.setPen(QPen(colour(self.theme, "warning")))
                elif line == current:
                    painter.setPen(QPen(colour(self.theme, "fg")))
                else:
                    painter.setPen(QPen(colour(self.theme, "gutter_fg")))

                painter.drawText(0, int(top), self.gutter.width() - 8,
                                 int(self.fontMetrics().height()), Qt.AlignRight, str(number + 1))

            block = block.next()
            top = bottom
            bottom = top + self.blockBoundingRect(block).height()
            number += 1

        painter.end()

    # ----------------------------------------------------------------- events

    def _on_cursor(self):
        # Current-line highlight.
        selection = QTextEdit.ExtraSelection()
        selection.format.setBackground(colour(self.theme, "current_line"))
        selection.format.setProperty(QTextFormat.FullWidthSelection, True)
        selection.cursor = self.textCursor()
        selection.cursor.clearSelection()
        self.setExtraSelections([selection])

        cursor = self.textCursor()
        self.cursorMoved.emit(cursor.blockNumber() + 1, cursor.positionInBlock() + 1)
        self._match_brackets()
        self.gutter.update()

    def _on_text(self):
        self._update_margins()
        self.gutter.update()
        self.modificationChanged.emit()

    def _match_brackets(self):
        text = self.toPlainText()
        position = self.textCursor().position()
        pairs = {"(": ")", "[": "]", "{": "}", ")": "(", "]": "[", "}": "{"}
        selections = list(self.extraSelections())

        if 0 <= position < len(text) and text[position] in pairs:
            other = self._find_partner(text, position, pairs[text[position]])
            if other is not None:
                for at in (position, other):
                    cursor = QTextCursor(self.document())
                    cursor.setPosition(at)
                    cursor.movePosition(QTextCursor.Right, QTextCursor.KeepAnchor)
                    selection = QTextEdit.ExtraSelection()
                    selection.cursor = cursor
                    selection.format.setBackground(colour(self.theme, "match"))
                    selections.append(selection)

        self.setExtraSelections(selections)

    @staticmethod
    def _find_partner(text, position, target):
        depth = 0
        pairs = {"(": ")", "[": "]", "{": "}"}
        opening = text[position]
        if opening in pairs:
            for index in range(position, len(text)):
                if text[index] == opening:
                    depth += 1
                elif text[index] == pairs[opening]:
                    depth -= 1
                    if depth == 0:
                        return index
        else:
            closing = {v: k for k, v in pairs.items()}.get(opening)
            if closing is None:
                return None
            for index in range(position, -1, -1):
                if text[index] == opening:
                    depth += 1
                elif text[index] == closing:
                    depth -= 1
                    if depth == 0:
                        return index
        _ = target
        return None

    def keyPressEvent(self, event: QKeyEvent):
        if self.popup.isVisible():
            key = event.key()
            if key in (Qt.Key_Down, Qt.Key_Up):
                self.popup.move_selection(1 if key == Qt.Key_Down else -1)
                return
            if key in (Qt.Key_Return, Qt.Key_Enter, Qt.Key_Tab):
                label = self.popup.current_label()
                if label:
                    self.insert_completion(label)
                self.popup.hide()
                return
            if key == Qt.Key_Escape:
                self.popup.hide()
                return

        if event.key() == Qt.Key_Tab and not event.modifiers():
            self.insertPlainText("    ")
            return

        if event.key() == Qt.Key_Backtab or (event.key() == Qt.Key_Tab and event.modifiers() & Qt.ShiftModifier):
            self._unindent()
            return

        if event.key() in (Qt.Key_Return, Qt.Key_Enter):
            self.newline_with_indent()
            return

        super().keyPressEvent(event)

        # Autocompletion after typing, or immediately after a dot.
        text = event.text()
        if text and (text.isalnum() or text in "_$."):
            if text == ".":
                self.request_completion()
            else:
                self.request_completion()

    def newline_with_indent(self):
        """Enter keeps the indentation and adds a level after an opening brace."""
        cursor = self.textCursor()
        line = cursor.block().text()
        indent = len(line) - len(line.lstrip(" "))
        stripped = line.rstrip()

        extras = 0
        if stripped.endswith("{") or stripped.endswith("(") or stripped.endswith("["):
            extras = 1

        self.insertPlainText("\n" + " " * (indent + extras * 4))

        # A closing brace already on the next line moves down with us.
        if extras and stripped.endswith("{"):
            rest = self.toPlainText()[self.textCursor().position():]
            if rest.startswith("}"):
                position = self.textCursor().position()
                self.insertPlainText("\n" + " " * indent)
                cursor = self.textCursor()
                cursor.setPosition(position)
                self.setTextCursor(cursor)

    def _unindent(self):
        cursor = self.textCursor()
        block = cursor.block()
        text = block.text()
        if text.startswith("    "):
            position = cursor.position()
            cursor.setPosition(block.position())
            cursor.movePosition(QTextCursor.Right, QTextCursor.KeepAnchor, 4)
            cursor.removeSelectedText()
            cursor.setPosition(max(block.position(), position - 4))
            self.setTextCursor(cursor)

    # ----------------------------------------------------------- completions

    def set_completions(self, items, start_offset):
        self._completion_items = items
        self._completion_start = start_offset

        prefix = self._prefix()
        matching = [item for item in items if item.get("label", "").lower().startswith(prefix.lower())]
        if not matching:
            matching = items
        if not matching:
            self.popup.hide()
            return

        self.popup.show_items(matching, self.theme, prefix)
        self._place_popup()

    def _prefix(self):
        cursor = self.textCursor()
        position = cursor.position()
        text = self.toPlainText()
        start = position
        while start > 0 and (text[start - 1].isalnum() or text[start - 1] in "_$"):
            start -= 1
        return text[start:position]

    def completion_offset(self):
        """Where the identifier being completed starts, in document offsets."""
        return self.textCursor().position() - len(self._prefix())

    def insert_completion(self, label):
        cursor = self.textCursor()
        position = cursor.position()
        prefix_length = len(self._prefix())
        cursor.setPosition(position - prefix_length)
        cursor.setPosition(position, QTextCursor.KeepAnchor)
        cursor.insertText(label)
        self.setTextCursor(cursor)

    def _place_popup(self):
        if not self.popup.isVisible():
            return
        rect = self.cursorRect()
        point = self.viewport().mapToGlobal(QPoint(int(rect.left()), int(rect.bottom()) + 2))
        width = self.popup.width()
        self.popup.move(point.x(), point.y())
        self.popup.setFixedWidth(max(320, width))

    def request_completion(self):
        self.completionRequested.emit(self.completion_offset())

    def show_hover(self, point, markdown):
        if not markdown:
            QToolTip.hideText()
            return
        QToolTip.showText(point, markdown, self)

    # --------------------------------------------------------------- problems

    def set_problems(self, problems):
        """problems: [(severity, line0, message)], plus underlines in the editor."""
        self.problems = problems
        self.strip.update()
        self.gutter.update()

        selections = list(self.extraSelections())
        for severity, line, message in problems:
            block = self.document().findBlockByNumber(line)
            if not block.isValid():
                continue
            selection = QTextEdit.ExtraSelection()
            selection.cursor = QTextCursor(block)
            selection.cursor.movePosition(QTextCursor.EndOfBlock, QTextCursor.KeepAnchor)
            colour_name = "error" if severity == 1 else "warning"
            selection.format.setUnderlineStyle(QTextCharFormat.SpellCheckUnderline
                                               if severity == 1 else QTextCharFormat.DotLine)
            selection.format.setUnderlineColor(colour(self.theme, colour_name))
            selections.append(selection)

        self.setExtraSelections(selections)

    def goto(self, line0, character=0):
        block = self.document().findBlockByNumber(line0)
        if not block.isValid():
            return
        cursor = QTextCursor(block)
        cursor.movePosition(QTextCursor.Right, QTextCursor.MoveAnchor, min(character, block.length() - 1))
        self.setTextCursor(cursor)
        self.centerCursor()

    def mouseMoveEvent(self, event):
        super().mouseMoveEvent(event)
        if event.modifiers() & Qt.ControlModifier:
            self.viewport().setCursor(Qt.PointingHandCursor)
        else:
            self.viewport().setCursor(Qt.IBeamCursor)
        self.hoverRequested.emit(event.pos().x(), event.pos().y(), event.globalPosition().toPoint())


class EditorArea(QWidget):
    """A tab's worth of editor plus a breadcrumb bar above it, like Rider's."""

    def __init__(self, path, text, theme, parent=None):
        super().__init__(parent)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(0)

        self.breadcrumbs = QLabel()
        self.breadcrumbs.setContentsMargins(8, 3, 8, 3)
        self.breadcrumbs.setStyleSheet(
            f"background: {theme['panel']}; color: {theme['fg_dim']}; border-bottom: 1px solid {theme['border']};")
        layout.addWidget(self.breadcrumbs)

        self.editor = Editor(path, text, theme, self)
        layout.addWidget(self.editor, 1)

        self.set_breadcrumbs([path.rsplit("/", 1)[-1]])

    def set_breadcrumbs(self, parts):
        self.breadcrumbs.setText("  ›  ".join(parts))

    def apply_theme(self, theme):
        self.editor.apply_theme(theme)
        self.breadcrumbs.setStyleSheet(
            f"background: {theme['panel']}; color: {theme['fg_dim']}; border-bottom: 1px solid {theme['border']};")
