"""TERN syntax highlighting.

A QSyntaxHighlighter over the same rules the lexer uses: the keyword table is
copied from TERN/Syntax/Lexer.cs, so an editor and the compiler cannot disagree
about which words are keywords. Strings, numbers, comments, struct and function
names, and `${}` interpolations each get a style from the theme.
"""

import re

from PySide6.QtCore import QRegularExpression
from PySide6.QtGui import QColor, QFont, QSyntaxHighlighter, QTextCharFormat

# Mirrors the keyword table in TERN/Syntax/Lexer.cs.
KEYWORDS = [
    "let", "const", "fn", "return", "if", "else", "while", "for", "in", "break",
    "continue", "struct", "match", "try", "catch", "finally", "throw", "import",
    "as", "nil", "true", "false", "loop", "this", "do", "and", "or", "not", "typeof",
]

LITERALS = {"nil", "true", "false"}


def format_for(theme, name, *, bold=False, italic=False):
    fmt = QTextCharFormat()
    fmt.setForeground(QColor(theme[name]))
    if bold:
        fmt.setFontWeight(QFont.Bold)
    if italic:
        fmt.setFontItalic(True)
    return fmt


class TernHighlighter(QSyntaxHighlighter):
    """Highlights one document. Rules are ordered; later rules win."""

    def __init__(self, document, theme):
        super().__init__(document)
        self._theme = theme
        self._rules = []
        self.build()

    def set_theme(self, theme):
        self._theme = theme
        self.build()
        self.rehighlight()

    def build(self):
        theme = self._theme
        self._rules = []

        keyword = format_for(theme, "keyword", bold=False)
        literal = format_for(theme, "keyword", bold=True)
        string = format_for(theme, "string")
        number = format_for(theme, "number")
        comment = format_for(theme, "comment", italic=True)
        function = format_for(theme, "function")
        struct = format_for(theme, "type", bold=True)
        annotation = format_for(theme, "member", italic=True)

        for word in KEYWORDS:
            style = literal if word in LITERALS else keyword
            self._rules.append((QRegularExpression(rf"\b{word}\b"), style))

        # Numbers, including floats and exponents.
        self._rules.append((QRegularExpression(r"\b\d+\.?\d*([eE][+-]?\d+)?\b"), number))

        # Call targets: a name immediately followed by a parenthesis.
        self._rules.append((QRegularExpression(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?=\()"), function))

        # Struct declarations and their construction: a capitalised name.
        self._rules.append((QRegularExpression(r"\b[A-Z][A-Za-z0-9_]*\b"), struct))

        # Built-in namespaces used as prefixes: math., fs., json., ...
        self._rules.append((QRegularExpression(r"\b(?:math|json|fs|os|text|lists|time|random|net|http|rcon|sftp|gl|d3d12)(?=\.)"),
                            annotation))

        # Annotations and imports.
        self._rules.append((QRegularExpression(r'"(std:[a-z0-9]+)"'), string))
        self._rules.append((QRegularExpression(r"\bimport\b\s*\"[^\"]*\""), string))

        # Comments last: they win over anything inside them.
        self._rules.append((QRegularExpression(r"//[^\n]*"), comment))
        self._rules.append((QRegularExpression(r"/\*"), comment))
        self._rules.append((QRegularExpression(r"\*/"), comment))

        self._comment = comment
        self._string = string
        self._interpolation = format_for(theme, "member")

    def highlightBlock(self, text):
        for expression, style in self._rules:
            iterator = expression.globalMatch(text)
            while iterator.hasNext():
                match = iterator.next()
                start = match.capturedStart()
                length = match.capturedLength()
                if length > 0:
                    self.setFormat(start, length, style)

        self.highlight_strings(text)
        self.highlight_multiline_comment(text)

    def highlight_strings(self, text):
        """Shared with the block-comment pass, so a string is never cut in two."""
        index = 0
        while True:
            start = text.find('"', index)
            if start < 0:
                break

            # Skip escapes.
            end = start + 1
            while end < len(text):
                if text[end] == "\\":
                    end += 2
                    continue
                if text[end] == '"':
                    break
                end += 1

            if end >= len(text):
                self.setFormat(start, len(text) - start, self._string)
                break

            self.setFormat(start, end - start + 1, self._string)

            # `${...}` inside the literal: give the expression its own colour so
            # it reads as code, which is what it is.
            for match in re.finditer(r"\$\{([^}]*)\}", text[start:end + 1]):
                self.setFormat(start + match.start(), match.end() - match.start(), self._interpolation)

            index = end + 1

    def highlight_multiline_comment(self, text):
        self.setCurrentBlockState(0)
        start = 0
        if self.previousBlockState() != 1:
            start = text.find("/*")
            if start < 0:
                return
        else:
            start = 0

        while start >= 0:
            end = text.find("*/", start)
            if end < 0:
                self.setCurrentBlockState(1)
                self.setFormat(start, len(text) - start, self._comment)
                break

            self.setFormat(start, end - start + 2, self._comment)
            start = text.find("/*", end + 2)


class MarkdownHighlighter(QSyntaxHighlighter):
    """Headings, code, links and emphasis, so a README reads as a document.

    Not a Markdown parser: ten rules that catch what the eye needs in the
    project's own pages, applied line by line.
    """

    def __init__(self, document, theme):
        super().__init__(document)
        self._theme = theme
        self.build()

    def set_theme(self, theme):
        self._theme = theme
        self.build()
        self.rehighlight()

    def build(self):
        theme = self._theme
        self._rules = [
            # A heading is the whole line, from the hashes to the end.
            (QRegularExpression(r"^#{1,6} .*$"), format_for(theme, "keyword", bold=True)),
            (QRegularExpression(r"^(===+|---+)$"), format_for(theme, "comment")),
            (QRegularExpression(r"`[^`]*`"), format_for(theme, "string")),
            (QRegularExpression(r"\*\*[^*]+\*\*"), format_for(theme, "type", bold=True)),
            (QRegularExpression(r"(?<!\*)\*[^*\s][^*]*\*"), format_for(theme, "member", italic=True)),
            (QRegularExpression(r"\[[^\]]*\]\([^)]*\)"), format_for(theme, "number")),
            (QRegularExpression(r"^\s*[-*+] "), format_for(theme, "keyword")),
            (QRegularExpression(r"^> .*$"), format_for(theme, "comment", italic=True)),
            (QRegularExpression(r"^```.*$"), format_for(theme, "comment")),
        ]

    def highlightBlock(self, text):
        for pattern, style in self._rules:
            iterator = pattern.globalMatch(text)
            while iterator.hasNext():
                match = iterator.next()
                self.setFormat(match.capturedStart(), match.capturedLength(), style)
