"""The Darcula palette and the two themes, as Qt stylesheets.

Rider's look is mostly restraint: four greys, one accent, and monospace for
code. These constants are the whole of it, so the palette can be swapped in one
place and every widget follows.
"""

from PySide6.QtGui import QColor, QFont, QFontDatabase

# Darcula, as JetBrains defines it.
DARKS = {
    "bg": "#2b2b2b",          # editor and window background
    "panel": "#3c3f41",       # tool windows, tabs, gutters
    "panel_alt": "#313335",   # list backgrounds
    "border": "#323232",      # separators
    "fg": "#a9b7c6",          # default text
    "fg_dim": "#808080",      # line numbers, hints
    "accent": "#3592c4",      # selection, focus, links
    "accent_dim": "#214283",  # inactive selection
    "keyword": "#cc7832",     # if, fn, let
    "string": "#6a8759",      # literals
    "number": "#6897bb",      # numbers
    "comment": "#808080",     # comments
    "type": "#a9b7c6",        # struct names (bold)
    "function": "#ffc66d",    # call targets
    "member": "#9876aa",      # fields and members
    "global": "#a9b7c6",      # built-in names
    "error": "#ff6b68",       # red squiggle, problems
    "warning": "#ffc66d",     # yellow squiggle
    "current_line": "#323232",
    "gutter_fg": "#606366",
    "selection": "#214283",
    "match": "#3a3d3f",
}

LIGHT = {
    "bg": "#ffffff",
    "panel": "#f2f2f2",
    "panel_alt": "#ffffff",
    "border": "#d1d1d1",
    "fg": "#1f1f1f",
    "fg_dim": "#8c8c8c",
    "accent": "#2e7fc1",
    "accent_dim": "#cfe3f5",
    "keyword": "#0033b3",
    "string": "#067d17",
    "number": "#1750eb",
    "comment": "#8c8c8c",
    "type": "#1f1f1f",
    "function": "#00627a",
    "member": "#7a3e9d",
    "global": "#1f1f1f",
    "error": "#c7221f",
    "warning": "#a67000",
    "current_line": "#eef3fa",
    "gutter_fg": "#a0a4a8",
    "selection": "#b6d7f9",
    "match": "#e8e8e8",
}

THEMES = {"darcula": DARKS, "light": LIGHT}


def monospace(size: int = 12) -> QFont:
    """The editor font: whatever the platform calls monospace, with a fallback."""
    font = QFontDatabase.systemFont(QFontDatabase.FixedFont)
    font.setPointSize(size)
    font.setStyleHint(QFont.Monospace)
    font.setFixedPitch(True)
    return font


def ui_font(size: int = 10) -> QFont:
    font = QFont()
    font.setPointSize(size)
    return font


def colour(theme: dict, name: str) -> QColor:
    """A palette entry as a QColor."""
    return QColor(theme[name])


def stylesheet(theme: dict) -> str:
    """The application stylesheet: Rider's flat panels, thin borders, no gradients."""
    return f"""
    QWidget {{
        background: {theme['bg']};
        color: {theme['fg']};
    }}
    QMainWindow, QDialog {{
        background: {theme['bg']};
    }}
    QMenuBar {{
        background: {theme['panel']};
        border-bottom: 1px solid {theme['border']};
    }}
    QMenuBar::item {{
        background: transparent;
        padding: 4px 10px;
    }}
    QMenuBar::item:selected {{
        background: {theme['accent_dim']};
    }}
    QMenu {{
        background: {theme['panel']};
        border: 1px solid {theme['border']};
        padding: 4px;
    }}
    QMenu::item {{
        padding: 4px 26px 4px 22px;
        border-radius: 3px;
    }}
    QMenu::item:selected {{
        background: {theme['accent']};
        color: #ffffff;
    }}
    QMenu::separator {{
        height: 1px;
        background: {theme['border']};
        margin: 4px 8px;
    }}
    QToolBar {{
        background: {theme['panel']};
        border-bottom: 1px solid {theme['border']};
        spacing: 2px;
        padding: 2px 4px;
    }}
    QToolBar QToolButton {{
        background: transparent;
        border: none;
        border-radius: 3px;
        padding: 4px 8px;
    }}
    QToolBar QToolButton:hover {{
        background: {theme['panel_alt']};
    }}
    QToolBar QToolButton:pressed {{
        background: {theme['accent_dim']};
    }}
    QToolButton#runButton {{
        background: #3a6b35;
        border: 1px solid #4d8a46;
        border-radius: 3px;
        padding: 3px 12px;
        color: #e8f5e9;
    }}
    QToolButton#runButton:hover {{
        background: #4a8243;
    }}
    QTabWidget::pane {{
        border: none;
        background: {theme['bg']};
    }}
    QTabBar {{
        background: {theme['panel']};
    }}
    QTabBar::tab {{
        background: {theme['panel']};
        color: {theme['fg_dim']};
        padding: 5px 14px;
        border-right: 1px solid {theme['border']};
    }}
    QTabBar::tab:selected {{
        background: {theme['bg']};
        color: {theme['fg']};
        border-bottom: 2px solid {theme['accent']};
    }}
    QTabBar::tab:hover:!selected {{
        background: {theme['panel_alt']};
    }}
    QSplitter::handle {{
        background: {theme['border']};
    }}
    QTreeView, QListView, QTableView {{
        background: {theme['panel_alt']};
        border: none;
        outline: none;
        alternate-background-color: {theme['panel']};
    }}
    QTreeView::item:selected, QListView::item:selected {{
        background: {theme['accent_dim']};
        color: {theme['fg']};
    }}
    QTreeView::item:hover, QListView::item:hover {{
        background: {theme['panel']};
    }}
    QHeaderView::section {{
        background: {theme['panel']};
        color: {theme['fg_dim']};
        border: none;
        border-bottom: 1px solid {theme['border']};
        padding: 4px;
    }}
    QLineEdit {{
        background: {theme['panel_alt']};
        border: 1px solid {theme['border']};
        border-radius: 3px;
        padding: 4px 6px;
        selection-background-color: {theme['accent']};
    }}
    QLineEdit:focus {{
        border: 1px solid {theme['accent']};
    }}
    QPlainTextEdit, QTextEdit {{
        background: {theme['bg']};
        border: none;
        selection-background-color: {theme['selection']};
    }}
    QStatusBar {{
        background: {theme['panel']};
        border-top: 1px solid {theme['border']};
        color: {theme['fg_dim']};
    }}
    QStatusBar::item {{
        border: none;
    }}
    QDockWidget {{
        titlebar-close-icon: none;
        titlebar-normal-icon: none;
    }}
    QDockWidget::title {{
        background: {theme['panel']};
        padding: 4px 8px;
        border-bottom: 1px solid {theme['border']};
    }}
    QScrollBar:vertical {{
        background: {theme['panel']};
        width: 12px;
        margin: 0;
    }}
    QScrollBar::handle:vertical {{
        background: #4b4f52;
        min-height: 24px;
        border-radius: 4px;
        margin: 2px;
    }}
    QScrollBar::add-line, QScrollBar::sub-line {{
        height: 0;
    }}
    QScrollBar:horizontal {{
        background: {theme['panel']};
        height: 12px;
        margin: 0;
    }}
    QScrollBar::handle:horizontal {{
        background: #4b4f52;
        min-width: 24px;
        border-radius: 4px;
        margin: 2px;
    }}
    QPushButton {{
        background: {theme['panel']};
        border: 1px solid {theme['border']};
        border-radius: 3px;
        padding: 5px 14px;
    }}
    QPushButton:hover {{
        background: {theme['panel_alt']};
    }}
    QPushButton:default {{
        border: 1px solid {theme['accent']};
    }}
    QComboBox, QSpinBox {{
        background: {theme['panel_alt']};
        border: 1px solid {theme['border']};
        border-radius: 3px;
        padding: 3px 6px;
    }}
    QComboBox QAbstractItemView {{
        background: {theme['panel']};
        selection-background-color: {theme['accent']};
        border: 1px solid {theme['border']};
    }}
    QToolTip {{
        background: {theme['panel']};
        color: {theme['fg']};
        border: 1px solid {theme['accent']};
        padding: 6px;
    }}
    QCheckBox::indicator {{
        width: 13px;
        height: 13px;
        border: 1px solid {theme['border']};
        background: {theme['panel_alt']};
    }}
    QCheckBox::indicator:checked {{
        background: {theme['accent']};
        border: 1px solid {theme['accent']};
    }}
    """
