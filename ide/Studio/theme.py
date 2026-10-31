"""The Darcula palette and the two themes, as Qt stylesheets.

Rider's look is mostly restraint: four greys, one accent, and monospace for
code. These constants are the whole of it, so the palette can be swapped in one
place and every widget follows.
"""

from PySide6.QtGui import QColor, QFont, QFontDatabase

from . import icons

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
    "added": "#8ec07c",       # diff additions, VCS "added"
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
    "added": "#2f7d32",
    "warning": "#a67000",
    "current_line": "#eef3fa",
    "gutter_fg": "#a0a4a8",
    "selection": "#b6d7f9",
    "match": "#e8e8e8",
}

# Eclipse's default light scheme: purple keywords, blue strings, green comments.
ECLIPSE = {
    "bg": "#ffffff",
    "panel": "#f3f3f3",
    "panel_alt": "#ffffff",
    "border": "#d6d6d6",
    "fg": "#000000",
    "fg_dim": "#7a7a7a",
    "accent": "#1a6fb5",
    "accent_dim": "#cfe4f7",
    "keyword": "#7f0055",
    "string": "#2a00ff",
    "number": "#2a00ff",
    "comment": "#3f7f5f",
    "type": "#000000",
    "function": "#000000",
    "member": "#0000c0",
    "global": "#000000",
    "error": "#c00000",
    "added": "#2f7d32",
    "warning": "#9a6a00",
    "current_line": "#e8f2fe",
    "gutter_fg": "#a8a8a8",
    "selection": "#b4d5fe",
    "match": "#e5e5e5",
}

# Visual Studio's Dark theme, as the editor ships it.
VS_DARK = {
    "bg": "#1e1e1e",
    "panel": "#252526",
    "panel_alt": "#2d2d30",
    "border": "#3f3f46",
    "fg": "#d4d4d4",
    "fg_dim": "#9d9d9d",
    "accent": "#007acc",
    "accent_dim": "#094771",
    "keyword": "#569cd6",
    "string": "#ce9178",
    "number": "#b5cea8",
    "comment": "#6a9955",
    "type": "#4ec9b0",
    "function": "#dcdcaa",
    "member": "#9cdcfe",
    "global": "#d4d4d4",
    "error": "#f44747",
    "added": "#6a9955",
    "warning": "#d7ba7d",
    "current_line": "#282828",
    "gutter_fg": "#858585",
    "selection": "#264f78",
    "match": "#3a3d41",
}

# Carbon: darker than Darcula, for people who work at night. Near-black
# surfaces, one blue, and nothing else competing for attention.
CARBON = {
    "bg": "#0f1114",
    "panel": "#15181c",
    "panel_alt": "#1a1e23",
    "border": "#262b31",
    "fg": "#c8d1da",
    "fg_dim": "#6b7580",
    "accent": "#4a9eff",
    "accent_dim": "#1b3a5c",
    "keyword": "#c792ea",
    "string": "#a5d6a7",
    "number": "#f2b76a",
    "comment": "#4f5b66",
    "type": "#8fd3f4",
    "function": "#e8c07d",
    "member": "#b0a8f0",
    "global": "#c8d1da",
    "error": "#ff5f5f",
    "added": "#7ddc8a",
    "warning": "#e8c07d",
    "current_line": "#15181c",
    "gutter_fg": "#48525c",
    "selection": "#22384f",
    "match": "#22303d",
}

THEMES = {
    "darcula": DARKS,
    "light": LIGHT,
    "eclipse": ECLIPSE,
    "vs-dark": VS_DARK,
    "carbon": CARBON,
}

# Menu order and the names shown in the View > Theme menu. Rider groups its
# schemes the same way: the two everybody knows, then the rest.
THEME_NAMES = {
    "darcula": "Darcula",
    "light": "IntelliJ Light",
    "eclipse": "Eclipse",
    "vs-dark": "Visual Studio Dark",
    "carbon": "Carbon (darker)",
}


def apply_to(app):
    """Give a QApplication the shared look: Fusion first, then the stylesheet.

    Every entry point calls this — the window, the tests, the screenshot
    generator — because a style set in one place and not another means the
    tests are checking a window nobody sees.
    """
    app.setStyle("Fusion")


def is_dark(theme):
    """Whether a palette is dark, by the background it puts behind the code."""
    return QColor(theme["bg"]).lightness() < 128


def blend(base, other, amount):
    """`base` moved `amount` (0..1) of the way towards `other`, as a hex string.

    Used for hover and pressed states, so a new palette gets them for free
    instead of declaring a dozen near-identical greys.
    """
    first, second = QColor(base), QColor(other)
    return QColor(
        int(first.red() + (second.red() - first.red()) * amount),
        int(first.green() + (second.green() - first.green()) * amount),
        int(first.blue() + (second.blue() - first.blue()) * amount),
    ).name()


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
    """The application stylesheet: flat surfaces, one accent, generous padding.

    Written for Qt's Fusion style, which is set in main.py. The native Windows
    style ignores most of what a stylesheet says about buttons and combos and
    draws its own beveled chrome around them, which is what made the window look
    like 2009 whatever the palette was.

    Every hover and pressed colour is derived from the palette rather than
    written out, so a new theme is a dozen entries and no surprises.
    """
    dark = is_dark(theme)
    # One step off the surface, for hover. Darker themes lighten, light ones
    # darken; both read as "the pointer is here" without a border.
    hover = blend(theme["panel_alt"], theme["fg"], 0.08 if dark else 0.06)
    pressed = blend(theme["panel_alt"], theme["fg"], 0.16 if dark else 0.12)
    subtle = blend(theme["bg"], theme["fg"], 0.04 if dark else 0.03)
    scroll = blend(theme["panel"], theme["fg"], 0.22 if dark else 0.18)
    scroll_hover = blend(theme["panel"], theme["fg"], 0.34 if dark else 0.28)
    control_border = blend(theme["panel_alt"], theme["fg"], 0.18 if dark else 0.16)
    accent_hover = blend(theme["accent"], "#ffffff", 0.12)
    accent_pressed = blend(theme["accent"], "#000000", 0.12)
    text_on_accent = "#ffffff" if QColor(theme["accent"]).lightness() < 170 else "#101010"

    return f"""
    QWidget {{
        background: {theme['bg']};
        color: {theme['fg']};
        font-size: 10pt;
    }}
    QMainWindow, QDialog {{
        background: {theme['bg']};
    }}
    /* ---- menus ---------------------------------------------------------- */
    QMenuBar {{
        background: {theme['panel']};
        border-bottom: 1px solid {theme['border']};
        padding: 1px 4px;
    }}
    QMenuBar::item {{
        background: transparent;
        padding: 5px 10px;
        border-radius: 4px;
        margin: 2px 1px;
    }}
    QMenuBar::item:selected {{
        background: {hover};
    }}
    QMenu {{
        background: {theme['panel']};
        border: 1px solid {theme['border']};
        border-radius: 6px;
        padding: 5px;
    }}
    QMenu::item {{
        padding: 6px 28px 6px 24px;
        border-radius: 4px;
    }}
    QMenu::item:selected {{
        background: {theme['accent']};
        color: {text_on_accent};
    }}
    QMenu::item:disabled {{
        color: {theme['fg_dim']};
    }}
    QMenu::separator {{
        height: 1px;
        background: {theme['border']};
        margin: 5px 10px;
    }}
    QMenu::icon {{
        margin-left: 6px;
    }}
    QMenu::right-arrow {{
        image: {icons.url("chevron-right", theme['fg_dim'], 14)};
        width: 14px;
        height: 14px;
    }}
    /* ---- toolbar -------------------------------------------------------- */
    QToolBar {{
        background: {theme['panel']};
        border: none;
        border-bottom: 1px solid {theme['border']};
        spacing: 2px;
        padding: 4px 6px;
    }}
    QToolBar::separator {{
        width: 1px;
        background: {theme['border']};
        margin: 5px 6px;
    }}
    QToolBar QToolButton {{
        background: transparent;
        border: none;
        border-radius: 5px;
        padding: 5px 9px;
        color: {theme['fg']};
    }}
    QToolBar QToolButton:hover {{
        background: {hover};
    }}
    QToolBar QToolButton:pressed {{
        background: {pressed};
    }}
    QToolBar QToolButton:disabled {{
        color: {theme['fg_dim']};
    }}
    QToolButton {{
        background: transparent;
        border: none;
        border-radius: 5px;
        padding: 4px 8px;
    }}
    QToolButton:hover {{
        background: {hover};
    }}
    /* The run button is the one filled control in the window. */
    QToolButton#runButton {{
        background: {theme['accent']};
        border: none;
        border-radius: 5px;
        padding: 5px 14px;
        color: {text_on_accent};
        font-weight: 600;
    }}
    QToolButton#runButton:hover {{
        background: {accent_hover};
    }}
    QToolButton#runButton:pressed {{
        background: {accent_pressed};
    }}
    /* ---- tabs ----------------------------------------------------------- */
    QTabWidget::pane {{
        border: none;
        background: {theme['bg']};
        top: -1px;
    }}
    QTabBar {{
        background: {theme['panel']};
        qproperty-drawBase: 0;
    }}
    QTabBar::tab {{
        background: transparent;
        color: {theme['fg_dim']};
        padding: 7px 14px;
        margin: 3px 1px 0 1px;
        border: none;
        border-top-left-radius: 6px;
        border-top-right-radius: 6px;
        min-width: 60px;
    }}
    QTabBar::tab:selected {{
        background: {theme['bg']};
        color: {theme['fg']};
        border-bottom: 2px solid {theme['accent']};
    }}
    QTabBar::tab:hover:!selected {{
        background: {hover};
        color: {theme['fg']};
    }}
    QTabBar::close-button {{
        subcontrol-position: right;
        image: {icons.url("close", theme['fg_dim'], 14)};
        padding: 2px;
        border-radius: 4px;
        margin-left: 4px;
    }}
    QTabBar::close-button:hover {{
        background: {pressed};
        image: {icons.url("close", theme['fg'], 14)};
    }}
    /* ---- splitters, docks ----------------------------------------------- */
    QSplitter::handle {{
        background: {theme['border']};
    }}
    QSplitter::handle:hover {{
        background: {theme['accent']};
    }}
    QDockWidget {{
        titlebar-close-icon: none;
        titlebar-normal-icon: none;
        border: 1px solid {theme['border']};
    }}
    QDockWidget::title {{
        background: {theme['panel']};
        padding: 6px 10px;
        border: none;
        border-bottom: 1px solid {theme['border']};
    }}
    QDockWidget > QWidget {{
        background: {theme['bg']};
    }}
    /* ---- lists and trees ------------------------------------------------ */
    QTreeView, QListView, QTableView {{
        background: {theme['panel_alt']};
        border: none;
        outline: none;
        alternate-background-color: {subtle};
    }}
    QTreeView::item, QListView::item {{
        padding: 4px 2px;
        border-radius: 3px;
    }}
    QTreeView::item:selected, QListView::item:selected {{
        background: {theme['accent_dim']};
        color: {theme['fg']};
    }}
    QTreeView::item:hover, QListView::item:hover {{
        background: {hover};
    }}
    QTreeView::branch {{
        background: transparent;
    }}
    QTreeView::branch:selected,
    QTreeView::branch:hover {{
        background: transparent;
    }}
    QTreeView::branch:has-children:!has-siblings:closed,
    QTreeView::branch:closed:has-children:has-siblings {{
        image: {icons.url("chevron-right", theme['fg_dim'], 14)};
    }}
    QTreeView::branch:open:has-children:!has-siblings,
    QTreeView::branch:open:has-children:has-siblings {{
        image: {icons.url("chevron-down", theme['fg_dim'], 14)};
    }}
    QHeaderView::section {{
        background: {theme['panel']};
        color: {theme['fg_dim']};
        border: none;
        border-bottom: 1px solid {theme['border']};
        padding: 6px 8px;
        font-weight: 600;
    }}
    /* ---- inputs --------------------------------------------------------- */
    QLineEdit, QComboBox, QSpinBox, QDoubleSpinBox {{
        background: {theme['panel_alt']};
        border: 1px solid {control_border};
        border-radius: 5px;
        padding: 5px 8px;
        min-height: 18px;
        selection-background-color: {theme['accent']};
        selection-color: {text_on_accent};
    }}
    QLineEdit:hover, QComboBox:hover, QSpinBox:hover {{
        border: 1px solid {blend(control_border, theme['fg'], 0.18)};
    }}
    QLineEdit:focus, QComboBox:focus, QSpinBox:focus, QDoubleSpinBox:focus {{
        border: 1px solid {theme['accent']};
    }}
    QLineEdit:disabled, QComboBox:disabled {{
        color: {theme['fg_dim']};
    }}
    QComboBox::drop-down {{
        subcontrol-origin: padding;
        subcontrol-position: center right;
        width: 20px;
        border: none;
        background: transparent;
    }}
    QComboBox::down-arrow {{
        image: {icons.url("chevron-down", theme['fg_dim'], 14)};
        width: 14px;
        height: 14px;
    }}
    QComboBox QAbstractItemView {{
        background: {theme['panel']};
        border: 1px solid {theme['border']};
        border-radius: 4px;
        padding: 4px;
        selection-background-color: {theme['accent']};
        selection-color: {text_on_accent};
        outline: none;
    }}
    QSpinBox::up-button, QSpinBox::down-button,
    QDoubleSpinBox::up-button, QDoubleSpinBox::down-button {{
        background: transparent;
        border: none;
        width: 16px;
    }}
    QSpinBox::up-button:hover, QSpinBox::down-button:hover {{
        background: {hover};
        border-radius: 3px;
    }}
    QSpinBox::up-arrow, QDoubleSpinBox::up-arrow {{
        image: {icons.url("chevron-up", theme['fg_dim'], 12)};
        width: 12px; height: 12px;
    }}
    QSpinBox::down-arrow, QDoubleSpinBox::down-arrow {{
        image: {icons.url("chevron-down", theme['fg_dim'], 12)};
        width: 12px; height: 12px;
    }}
    QPlainTextEdit, QTextEdit {{
        background: {theme['bg']};
        border: none;
        selection-background-color: {theme['selection']};
        selection-color: {theme['fg']};
    }}
    QTextBrowser {{
        background: {theme['bg']};
        border: none;
    }}
    /* ---- buttons -------------------------------------------------------- */
    QPushButton {{
        background: {theme['panel_alt']};
        border: 1px solid {control_border};
        border-radius: 5px;
        padding: 6px 16px;
        min-height: 18px;
    }}
    QPushButton:hover {{
        background: {hover};
    }}
    QPushButton:pressed {{
        background: {pressed};
    }}
    QPushButton:default {{
        background: {theme['accent']};
        border: 1px solid {theme['accent']};
        color: {text_on_accent};
        font-weight: 600;
    }}
    QPushButton:default:hover {{
        background: {accent_hover};
    }}
    QPushButton:disabled {{
        color: {theme['fg_dim']};
        background: {theme['panel']};
    }}
    /* ---- checkboxes and radios: flat, no bevel -------------------------- */
    QCheckBox, QRadioButton {{
        spacing: 7px;
        padding: 3px 0;
    }}
    QCheckBox::indicator {{
        width: 15px;
        height: 15px;
        border: 1px solid {control_border};
        border-radius: 4px;
        background: {theme['panel_alt']};
    }}
    QCheckBox::indicator:hover {{
        border: 1px solid {theme['accent']};
    }}
    QCheckBox::indicator:checked {{
        background: {theme['accent']};
        border: 1px solid {theme['accent']};
        image: {icons.url("check", "#ffffff", 14)};
    }}
    QCheckBox::indicator:indeterminate {{
        background: {theme['accent']};
        border: 1px solid {theme['accent']};
        image: {icons.url("square", "#ffffff", 10)};
    }}
    QRadioButton::indicator {{
        width: 15px;
        height: 15px;
        border: 1px solid {control_border};
        border-radius: 8px;
        background: {theme['panel_alt']};
    }}
    QRadioButton::indicator:checked {{
        background: {theme['accent']};
        border: 4px solid {theme['panel_alt']};
    }}
    /* ---- status bar ----------------------------------------------------- */
    QStatusBar {{
        background: {theme['panel']};
        border-top: 1px solid {theme['border']};
        color: {theme['fg_dim']};
        padding: 2px 8px;
    }}
    QStatusBar::item {{
        border: none;
    }}
    QStatusBar QLabel {{
        color: {theme['fg_dim']};
        padding: 0 6px;
    }}
    /* ---- scrollbars: thin, no arrows ------------------------------------ */
    QScrollBar:vertical {{
        background: transparent;
        width: 12px;
        margin: 0;
    }}
    QScrollBar::handle:vertical {{
        background: {scroll};
        min-height: 28px;
        border-radius: 3px;
        margin: 3px 3px;
    }}
    QScrollBar::handle:vertical:hover {{
        background: {scroll_hover};
    }}
    QScrollBar::add-line, QScrollBar::sub-line {{
        height: 0;
        width: 0;
    }}
    QScrollBar::add-page, QScrollBar::sub-page {{
        background: transparent;
    }}
    QScrollBar:horizontal {{
        background: transparent;
        height: 12px;
        margin: 0;
    }}
    QScrollBar::handle:horizontal {{
        background: {scroll};
        min-width: 28px;
        border-radius: 3px;
        margin: 3px 3px;
    }}
    QScrollBar::handle:horizontal:hover {{
        background: {scroll_hover};
    }}
    /* ---- tooltips and misc ---------------------------------------------- */
    QToolTip {{
        background: {theme['panel']};
        color: {theme['fg']};
        border: 1px solid {control_border};
        border-radius: 4px;
        padding: 6px 8px;
    }}
    QProgressBar {{
        background: {theme['panel_alt']};
        border: none;
        border-radius: 3px;
        height: 6px;
        text-align: center;
    }}
    QProgressBar::chunk {{
        background: {theme['accent']};
        border-radius: 3px;
    }}
    QGroupBox {{
        border: 1px solid {theme['border']};
        border-radius: 6px;
        margin-top: 10px;
        padding-top: 8px;
    }}
    QGroupBox::title {{
        subcontrol-origin: margin;
        left: 10px;
        padding: 0 4px;
        color: {theme['fg_dim']};
    }}
    """
