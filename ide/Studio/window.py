"""TERN Studio: the Rider-shaped shell.

The layout is Rider's, borrowed deliberately:

    menu bar          File Edit View Navigate Code Run Tools Help
    toolbar           run / debug / stop, run configuration, then the file path
    tool windows      left: Project and Structure; bottom: Run, Problems, Terminal
    editor tabs       one per file, with a breadcrumb bar under the tabs
    status bar        problems, position, encoding, language, server state

Everything the editor knows about TERN comes from the syntax server over LSP;
the window itself contains no language knowledge beyond the keyword list the
highlighter needs to colour text.
"""

import json
import os
import sys

from PySide6.QtCore import QEvent, QProcess, QSize, Qt, QTimer
from PySide6.QtGui import QAction, QKeySequence
from PySide6.QtWidgets import (QApplication, QComboBox, QDockWidget, QFileDialog,
                               QInputDialog, QLabel, QListWidget, QMainWindow,
                               QMessageBox, QPlainTextEdit, QSizePolicy, QTabWidget,
                               QToolBar, QToolButton, QWidget)

from .editor import EditorArea
from .lsp import LanguageServer, path_for, uri_for
from .panels import (ChangesPanel, ConsolePanel, DebugPanel, DiffPanel,
                     FindInPathPanel, ProblemsPanel, ProjectPanel,
                     SearchEverywhereDialog, StructurePanel, SymbolDialog,
                     TerminalPanel)
from .debugger import DebugSession
from .vcs import parse_diff
from .theme import THEMES, monospace, stylesheet


class StudioWindow(QMainWindow):
    def __init__(self, root, launcher=None, theme="darcula", settings=None):
        """Builds the shell.

        `settings` is an optional QSettings; when given, the theme, the window
        size and the open files are remembered between runs. Tests pass none,
        so they always start from the same state.
        """
        super().__init__()
        self.settings = settings
        self.root = os.path.abspath(root)
        # The interpreter and the syntax server belong to the installation,
        # not the project: opening somebody else's directory must not take the
        # language server with it.
        self.install = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
        self.launcher = launcher or self._find_launcher()
        self.theme_name = theme
        self.theme = THEMES[theme]
        self.tabs = {}                       # path -> EditorArea
        self.diff_tabs = {}                  # "work:/abs/path" -> DiffPanel
        self._recent = []                    # paths, most recent first
        self.debug_session = None            # the DebugSession, while there is one
        self._outline_cache = {}             # path -> the last document symbols
        self.pending_hover = (None, None)
        self.run_process = None
        self.log_lines = []

        self.setWindowTitle("TERN Studio")
        self.resize(1440, 900)
        self.setMinimumSize(900, 600)

        self._build_central()
        self._build_docks()
        self._build_actions()
        self._build_menus()
        self._build_toolbar()
        self._build_status()
        self._apply_theme()
        self._restore()
        self.console.write("TERN Studio\n", info=True)
        self.console.write(f"Project: {self.root}\n", info=True)
        self.console.write("Run a file with F5, or compile it with the native backend with Ctrl+F5.\n\n",
                           info=True)

        self._start_server()
        self._start_timer()
        self.refresh_vcs()

        app = QApplication.instance()
        if app is not None:
            app.aboutToQuit.connect(self._shutdown)
        self._open_start_files()

    # ------------------------------------------------------------------ build

    def _build_central(self):
        self.tabs_widget = QTabWidget()
        self.tabs_widget.setTabsClosable(True)
        self.tabs_widget.setMovable(True)
        self.tabs_widget.setDocumentMode(True)
        self.tabs_widget.tabCloseRequested.connect(self.close_tab)
        self.tabs_widget.currentChanged.connect(self._tab_changed)
        self.setCentralWidget(self.tabs_widget)

        self.welcome = QPlainTextEdit()
        self.welcome.setReadOnly(True)
        self.welcome.setFont(monospace(12))
        self.welcome.setPlainText(
            "TERN Studio\n"
            "===========\n\n"
            "Open a file from the Project tool window, or press Ctrl+Shift+O to open one.\n\n"
            "  Ctrl+Shift+A    Search everywhere\n"
            "  Ctrl+E          Recent files\n"
            "  Ctrl+P          Go to file\n"
            "  F12             Go to definition\n"
            "  Ctrl+B          Go to declaration\n"
            "  Alt+F7          Find usages\n"
            "  Shift+F6        Rename\n"
            "  Ctrl+Alt+L      Reformat code\n"
            "  Ctrl+Space      Code completion\n"
            "  Ctrl+F / Ctrl+H Find and replace\n"
            "  Ctrl+/          Comment the line\n"
            "  Ctrl+D          Duplicate the line\n"
            "  Alt+Shift+Up    Move the line up (Down for down)\n"
            "  Ctrl+G          Go to line\n"
            "  F5              Run\n"
            "  Ctrl+F5         Compile with the native backend and run\n"
            "  Alt+0           Commit window\n\n"
            "The editor is powered by a language server: diagnostics, completion,\n"
            "hover documentation, navigation and rename all come from the same\n"
            "parser and analyser that compile the program.\n")
        self.tabs_widget.addTab(self.welcome, "Welcome")

    def _build_docks(self):
        # Left: project and structure, stacked as Rider stacks them.
        self.project_panel = ProjectPanel(self.root, self.theme)
        self.project_panel.openRequested.connect(self.open_file)

        self.project_dock = QDockWidget("Project", self)
        self.project_dock.setObjectName("projectDock")
        self.project_dock.setWidget(self.project_panel)
        self.project_dock.setAllowedAreas(Qt.LeftDockWidgetArea | Qt.RightDockWidgetArea)
        self.addDockWidget(Qt.LeftDockWidgetArea, self.project_dock)

        self.structure_panel = StructurePanel(self.theme)
        self.structure_panel.navigate.connect(self._goto_offset)
        self.structure_dock = QDockWidget("Structure", self)
        self.structure_dock.setObjectName("structureDock")
        self.structure_dock.setWidget(self.structure_panel)
        self.addDockWidget(Qt.LeftDockWidgetArea, self.structure_dock)

        # Bottom: run, problems, terminal.
        self.console = ConsolePanel(self.theme)
        self.console_dock = QDockWidget("Run", self)
        self.console_dock.setObjectName("runDock")
        self.console_dock.setWidget(self.console)
        self.addDockWidget(Qt.BottomDockWidgetArea, self.console_dock)

        self.problems_panel = ProblemsPanel(self.theme)
        self.problems_panel.navigate.connect(self._goto_path)
        self.problems_dock = QDockWidget("Problems", self)
        self.problems_dock.setObjectName("problemsDock")
        self.problems_dock.setWidget(self.problems_panel)
        self.addDockWidget(Qt.BottomDockWidgetArea, self.problems_dock)

        self.terminal = TerminalPanel(self.theme)
        self.terminal_dock = QDockWidget("Terminal", self)
        self.terminal_dock.setObjectName("terminalDock")
        self.terminal_dock.setWidget(self.terminal)
        self.addDockWidget(Qt.BottomDockWidgetArea, self.terminal_dock)

        self.find_panel = FindInPathPanel(self.root, self.theme)
        self.find_panel.openRequested.connect(self._goto_line_column)
        self.find_dock = QDockWidget("Find", self)
        self.find_dock.setObjectName("findDock")
        self.find_dock.setWidget(self.find_panel)
        self.addDockWidget(Qt.BottomDockWidgetArea, self.find_dock)

        self.debug_panel = DebugPanel(self.theme)
        self.debug_panel.frameSelected.connect(self._show_frame_variables)
        self.debug_panel.evaluate_callback = self._evaluate_expression
        self.debug_dock = QDockWidget("Debug", self)
        self.debug_dock.setObjectName("debugDock")
        self.debug_dock.setWidget(self.debug_panel)
        self.addDockWidget(Qt.BottomDockWidgetArea, self.debug_dock)

        self.changes_panel = ChangesPanel(self.root, self.theme)
        self.changes_panel.openRequested.connect(self.open_file)
        self.changes_panel.diffRequested.connect(self.show_diff)
        self.changes_panel.committed.connect(self._on_committed)
        self.changes_dock = QDockWidget("Commit", self)
        self.changes_dock.setObjectName("changesDock")
        self.changes_dock.setWidget(self.changes_panel)
        self.addDockWidget(Qt.BottomDockWidgetArea, self.changes_dock)

        self.setTabPosition(Qt.BottomDockWidgetArea, QTabWidget.North)
        for dock in (self.console_dock, self.problems_dock, self.terminal_dock,
                     self.structure_dock, self.changes_dock, self.debug_dock,
                     self.find_dock):
            dock.setTitleBarWidget(QWidget())
        self.tabifyDockWidget(self.console_dock, self.problems_dock)
        self.tabifyDockWidget(self.problems_dock, self.terminal_dock)
        self.tabifyDockWidget(self.terminal_dock, self.debug_dock)
        self.tabifyDockWidget(self.debug_dock, self.changes_dock)
        self.tabifyDockWidget(self.changes_dock, self.find_dock)
        self.console_dock.raise_()

        self.resizeDocks([self.project_dock, self.structure_dock], [260, 260], Qt.Horizontal)
        # The Commit window carries a message box, so it needs more height than
        # the three list windows it is tabified with.
        self.resizeDocks([self.console_dock, self.problems_dock, self.terminal_dock],
                         [170, 170, 170], Qt.Vertical)
        self.resizeDocks([self.changes_dock], [250], Qt.Vertical)
        self.resizeDocks([self.debug_dock], [250], Qt.Vertical)
        self.resizeDocks([self.find_dock], [250], Qt.Vertical)

    def _action(self, text, shortcut=None, slot=None, checkable=False):
        action = QAction(text, self)
        if shortcut:
            action.setShortcut(QKeySequence(shortcut))
        if slot:
            action.triggered.connect(slot)
        action.setCheckable(checkable)
        return action

    def _build_actions(self):
        self.actions_map = {
            "new": self._action("New File...", "Ctrl+N", self.new_file),
            "open": self._action("Open...", "Ctrl+O", self.open_file_dialog),
            "open_recent": self._action("Open Recent", None, None),
            "save": self._action("Save", "Ctrl+S", self.save_current),
            "save_all": self._action("Save All", "Ctrl+Shift+S", self.save_all),
            "close_tab": self._action("Close Tab", "Ctrl+W", lambda: self.close_tab(self.tabs_widget.currentIndex())),
            "exit": self._action("Exit", "Ctrl+Q", self.close),
            "undo": self._action("Undo", "Ctrl+Z", lambda: self._editor() and self._editor().undo()),
            "redo": self._action("Redo", "Ctrl+Shift+Z", lambda: self._editor() and self._editor().redo()),
            "cut": self._action("Cut", "Ctrl+X", lambda: self._editor() and self._editor().cut()),
            "copy": self._action("Copy", "Ctrl+C", lambda: self._editor() and self._editor().copy()),
            "paste": self._action("Paste", "Ctrl+V", lambda: self._editor() and self._editor().paste()),
            "select_all": self._action("Select All", "Ctrl+A", lambda: self._editor() and self._editor().selectAll()),
            "find": self._action("Find...", "Ctrl+F", self.find),
            "replace": self._action("Replace...", "Ctrl+H", lambda: self.find(with_replace=True)),
            "find_next": self._action("Find Next", "F3", self.find_next),
            "find_previous": self._action("Find Previous", "Shift+F3", self.find_previous),
            "comment": self._action("Comment with Line Comment", "Ctrl+/", self.toggle_comment),
            "duplicate": self._action("Duplicate Line or Selection", "Ctrl+D", self.duplicate_line),
            "move_up": self._action("Move Line Up", "Alt+Shift+Up", lambda: self.move_lines(-1)),
            "move_down": self._action("Move Line Down", "Alt+Shift+Down", lambda: self.move_lines(1)),
            "goto_line": self._action("Go to Line...", "Ctrl+G", self.goto_line),
            "recent": self._action("Recent Files...", "Ctrl+E", self.recent_files),
            "complete": self._action("Code Completion", "Ctrl+Space", self.complete),
            "goto_definition": self._action("Go to Definition", "F12", self.goto_definition),
            "find_usages": self._action("Find Usages", "Alt+F7", self.find_usages),
            "rename": self._action("Rename...", "Shift+F6", self.rename),
            "format": self._action("Reformat Code", "Ctrl+Alt+L", self.format_code),
            "search_everywhere": self._action("Search Everywhere", "Ctrl+Shift+A", self.search_everywhere),
            "goto_file": self._action("Go to File...", "Ctrl+Shift+N", self.goto_file),
            "find_in_path": self._action("Find in Path...", "Ctrl+Shift+F",
                                         lambda: self.show_find_in_path()),
            "goto_symbol_in_file": self._action("Go to Symbol in File...", "Ctrl+F12",
                                                self.goto_symbol_in_file),
            "keymap": self._action("Default Keymap", None, self.show_keymap),
            "goto_symbol": self._action("Go to Symbol...", "Ctrl+Alt+Shift+N", self.goto_symbol),
            "problems": self._action("Problems", "Alt+6", lambda: self._show_dock(self.problems_dock)),
            "run": self._action("Run", "F5", self.run_current),
            "compile": self._action("Compile with Native Backend and Run", "Ctrl+F5", self.compile_and_run),
            "stop": self._action("Stop", "Ctrl+F2", self.stop_run),
            "check": self._action("Check File", "Ctrl+Alt+F5", self.check_file),
            "debug": self._action("Debug", "Shift+F9", self.debug_current),
            "resume": self._action("Resume Program", "Alt+F9", self.debug_resume),
            "step_over": self._action("Step Over", "F8", self.debug_step_over),
            "step_into": self._action("Step Into", "F7", self.debug_step_into),
            "step_out": self._action("Step Out", "Shift+F8", self.debug_step_out),
            "toggle_breakpoint": self._action("Toggle Breakpoint", "Ctrl+F8",
                                              self.toggle_breakpoint),
            "debug_window": self._action("Debug", "Alt+5",
                                         lambda: self._show_dock(self.debug_dock)),
            "terminal": self._action("Terminal", "Alt+F12", lambda: self._show_dock(self.terminal_dock)),
            "commit": self._action("Commit", "Alt+0", lambda: self._show_dock(self.changes_dock)),
            "refresh_vcs": self._action("Refresh VCS", "Ctrl+Alt+F5", self.refresh_vcs),
            "diff_selected": self._action("Show Diff", "Ctrl+Shift+D", self._diff_selected),
            "project": self._action("Project", "Alt+1", lambda: self._show_dock(self.project_dock)),
            "structure": self._action("Structure", "Alt+7", lambda: self._show_dock(self.structure_dock)),
            "run_window": self._action("Run Window", "Alt+4", lambda: self._show_dock(self.console_dock)),
            "theme_darcula": self._action("Darcula", None, lambda: self.set_theme("darcula"), checkable=True),
            "theme_light": self._action("Light", None, lambda: self.set_theme("light"), checkable=True),
            "docs": self._action("Standard Library Reference", None, self.open_docs),
            "about": self._action("About TERN Studio", None, self.about),
        }
        self.actions_map["theme_darcula"].setChecked(self.theme_name == "darcula")
        self.actions_map["theme_light"].setChecked(self.theme_name == "light")

    def _build_menus(self):
        bar = self.menuBar()
        a = self.actions_map

        file_menu = bar.addMenu("&File")
        file_menu.addAction(a["new"])
        file_menu.addAction(a["open"])
        file_menu.addSeparator()
        file_menu.addAction(a["save"])
        file_menu.addAction(a["save_all"])
        file_menu.addSeparator()
        file_menu.addAction(a["close_tab"])
        file_menu.addSeparator()
        file_menu.addAction(a["exit"])

        edit_menu = bar.addMenu("&Edit")
        for name in ("undo", "redo"):
            edit_menu.addAction(a[name])
        edit_menu.addSeparator()
        for name in ("cut", "copy", "paste"):
            edit_menu.addAction(a[name])
        edit_menu.addSeparator()
        edit_menu.addSeparator()
        edit_menu.addAction(a["find"])
        edit_menu.addAction(a["replace"])
        edit_menu.addAction(a["find_next"])
        edit_menu.addAction(a["find_previous"])
        edit_menu.addSeparator()
        edit_menu.addAction(a["find_in_path"])
        edit_menu.addSeparator()
        edit_menu.addAction(a["comment"])
        edit_menu.addAction(a["duplicate"])
        edit_menu.addAction(a["move_up"])
        edit_menu.addAction(a["move_down"])
        edit_menu.addSeparator()
        edit_menu.addAction(a["select_all"])

        view_menu = bar.addMenu("&View")
        view_menu.addAction(a["project"])
        view_menu.addAction(a["structure"])
        view_menu.addAction(a["run_window"])
        view_menu.addAction(a["problems"])
        view_menu.addAction(a["terminal"])
        view_menu.addAction(a["debug_window"])
        view_menu.addAction(a["commit"])
        view_menu.addSeparator()
        theme_menu = view_menu.addMenu("Theme")
        theme_menu.addAction(a["theme_darcula"])
        theme_menu.addAction(a["theme_light"])

        navigate_menu = bar.addMenu("&Navigate")
        navigate_menu.addAction(a["goto_line"])
        navigate_menu.addAction(a["recent"])
        navigate_menu.addAction(a["goto_file"])
        navigate_menu.addAction(a["goto_symbol"])
        navigate_menu.addAction(a["goto_symbol_in_file"])
        navigate_menu.addAction(a["search_everywhere"])
        navigate_menu.addSeparator()
        navigate_menu.addAction(a["goto_definition"])
        navigate_menu.addAction(a["find_usages"])

        code_menu = bar.addMenu("&Code")
        code_menu.addAction(a["complete"])
        code_menu.addAction(a["rename"])
        code_menu.addSeparator()
        code_menu.addAction(a["format"])

        run_menu = bar.addMenu("&Run")
        run_menu.addAction(a["run"])
        run_menu.addAction(a["debug"])
        run_menu.addAction(a["compile"])
        run_menu.addAction(a["stop"])
        run_menu.addSeparator()
        run_menu.addAction(a["resume"])
        run_menu.addAction(a["step_over"])
        run_menu.addAction(a["step_into"])
        run_menu.addAction(a["step_out"])
        run_menu.addSeparator()
        run_menu.addAction(a["toggle_breakpoint"])
        run_menu.addSeparator()
        run_menu.addAction(a["check"])

        tools_menu = bar.addMenu("&Tools")
        tools_menu.addAction(a["search_everywhere"])
        tools_menu.addAction(a["refresh_vcs"])
        tools_menu.addAction(a["diff_selected"])
        tools_menu.addAction(a["docs"])

        help_menu = bar.addMenu("&Help")
        help_menu.addAction(a["docs"])
        help_menu.addAction(a["keymap"])
        help_menu.addAction(a["about"])

    def _build_toolbar(self):
        bar = QToolBar("Main")
        # saveState() only records widgets that have a name.
        bar.setObjectName("mainToolBar")
        self.toolbar = bar
        bar.setMovable(False)
        bar.setIconSize(QSize(16, 16))
        self.addToolBar(bar)

        a = self.actions_map

        run_button = QToolButton()
        run_button.setText("▶  Run")
        run_button.setObjectName("runButton")
        run_button.setToolTip("Run the current file (F5)")
        run_button.clicked.connect(self.run_current)
        bar.addWidget(run_button)

        debug_button = QToolButton()
        debug_button.setText("Debug")
        debug_button.setToolTip("Debug the current file (Shift+F9)")
        debug_button.clicked.connect(self.debug_current)
        bar.addWidget(debug_button)

        compile_button = QToolButton()
        compile_button.setText("⚙  Compile")
        compile_button.setToolTip("Compile with the native backend and run (Ctrl+F5)")
        compile_button.clicked.connect(self.compile_and_run)
        bar.addWidget(compile_button)

        stop_button = QToolButton()
        stop_button.setText("■")
        stop_button.setToolTip("Stop (Ctrl+F2)")
        stop_button.clicked.connect(self.stop_run)
        bar.addWidget(stop_button)

        bar.addSeparator()

        self.configuration = QComboBox()
        self.configuration.addItems(["Run current file", "Check current file"])
        self.configuration.setFixedWidth(190)
        bar.addWidget(self.configuration)

        bar.addSeparator()

        for name, text in (("search_everywhere", "⌕  Search Everywhere"),
                           ("format", "≡  Reformat"),
                           ("problems", "!  Problems")):
            button = QToolButton()
            button.setText(text)
            button.setToolTip(a[name].toolTip() or name)
            button.clicked.connect(a[name].trigger)
            bar.addWidget(button)

        spacer = QWidget()
        spacer.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Preferred)
        bar.addWidget(spacer)

        self.path_label = QLabel("")
        self.path_label.setStyleSheet(f"color: {self.theme['fg_dim']}; padding-right: 8px;")
        bar.addWidget(self.path_label)

    def _build_status(self):
        status = self.statusBar()
        self.status_problems = QLabel("No problems")
        self.status_position = QLabel("Ln 1, Col 1")
        self.status_language = QLabel("TERN")
        self.status_encoding = QLabel("UTF-8")
        self.status_branch = QLabel("")
        self.status_server = QLabel("syntax server: starting")

        status.addWidget(self.status_problems)
        status.addPermanentWidget(self.status_branch)
        status.addPermanentWidget(self.status_position)
        status.addPermanentWidget(self.status_language)
        status.addPermanentWidget(self.status_encoding)
        status.addPermanentWidget(self.status_server)

    # ---------------------------------------------------------------- server

    def _start_server(self):
        command = self._server_command()
        self.server = LanguageServer(command, self.root, self)
        self.server.diagnostics.connect(self._on_diagnostics)
        self.server.ready.connect(self._on_server_ready)
        self.server.log.connect(self._append_log)
        self.server.start()

    def _find_launcher(self):
        for base in (self.root, self.install):
            candidate = os.path.join(base, "tools", "tern")
            if os.path.exists(candidate):
                return candidate
        return "tern"

    def _server_command(self):
        # The same launcher tools/tern uses: build/dotnet is the runtime copied
        # next to the assemblies. The toolchain directory itself holds the .NET
        # root, not an executable, so it cannot be used directly.
        base = self.install
        assembly = os.path.join(base, "build", "TERN.Ide.dll")
        if not os.path.exists(assembly):
            base = self.root
            assembly = os.path.join(base, "build", "TERN.Ide.dll")

        launcher = os.path.join(base, "build", "dotnet")
        if os.path.exists(launcher):
            return [launcher, assembly]

        toolchain = os.environ.get("TERN_TOOLCHAIN_DIR", "/tmp/tern-toolchain")
        candidate = os.path.join(toolchain, "dotnet", "dotnet")
        if os.path.exists(candidate):
            return [candidate, assembly]

        return ["dotnet", assembly]

    def _on_server_ready(self, ok):
        self.status_server.setText("syntax server: ready" if ok else "syntax server: unavailable")
        if ok:
            for path in list(self.tabs):
                text = self.tabs[path].editor.toPlainText()
                self.server.did_open(uri_for(path), text)
                self._request_outline(path)

    def _append_log(self, text):
        self.log_lines.append(text)
        if len(self.log_lines) > 400:
            self.log_lines = self.log_lines[-200:]
        self.terminal.write(f"[ls] {text}\n", info=True) if False else None

    def _start_timer(self):
        # Analysis is requested on every edit and answered immediately; the
        # timer is only here so the outline is refreshed after the user stops
        # typing rather than on every keystroke.
        self._outline_timer = QTimer(self)
        self._outline_timer.setSingleShot(True)
        self._outline_timer.setInterval(250)
        self._outline_timer.timeout.connect(self._refresh_outline)

    def _refresh_outline(self):
        area = self._area()
        if area is not None and hasattr(self, "server"):
            self._request_outline(area.editor.path)

    # ------------------------------------------------------------------ files

    def _open_start_files(self):
        restored = []
        if self.settings is not None:
            if os.path.normpath(str(self.settings.value("project", "", str))) == \
                    os.path.normpath(self.root):
                remembered = self.settings.value("open_files", [], list) or []
                restored = [path for path in remembered
                            if isinstance(path, str) and os.path.exists(path)]
            current = self.settings.value("current_file", "", str)

        if restored:
            for path in restored[:12]:
                self.open_file(path)
            if current in self.tabs:
                self.tabs_widget.setCurrentWidget(self.tabs[current])
            return

        starters = []
        for name in ("examples/triage.tern", "examples/fizzbuzz.tern"):
            candidate = os.path.join(self.root, name)
            if os.path.exists(candidate):
                starters.append(candidate)

        if not starters:
            # Not the TERN repository: open the first script in the project.
            try:
                names = sorted(os.listdir(self.root))
            except OSError:
                names = []
            for name in names:
                candidate = os.path.join(self.root, name)
                if name.endswith(".tern") and os.path.isfile(candidate):
                    starters.append(candidate)
                    break

        for path in starters[:1]:
            self.open_file(path)

    def _editor(self):
        widget = self.tabs_widget.currentWidget()
        return widget.editor if isinstance(widget, EditorArea) else None

    def _area(self):
        widget = self.tabs_widget.currentWidget()
        return widget if isinstance(widget, EditorArea) else None

    def open_file(self, path):
        path = os.path.abspath(path)
        if path in self.tabs:
            self.tabs_widget.setCurrentWidget(self.tabs[path])
            return

        try:
            with open(path, "r", encoding="utf-8") as handle:
                text = handle.read()
        except OSError as error:
            QMessageBox.warning(self, "TERN Studio", f"Could not open {path}:\n{error}")
            return

        area = EditorArea(path, text, self.theme)
        editor = area.editor
        editor.cursorMoved.connect(self._cursor_moved)
        editor.modificationChanged.connect(self._tab_changed)
        editor.completionRequested.connect(lambda offset: self._completion(editor, offset))
        editor.breakpointsChanged.connect(
            lambda _lines: self.debug_session.set_breakpoints(sorted(editor.breakpoints))
            if self.debug_session is not None and self.debug_session.running else None)
        editor.hoverRequested.connect(lambda x, y, point: self._hover(editor, point))
        editor.setStyleSheet(
            f"QPlainTextEdit {{ background: {self.theme['bg']}; color: {self.theme['fg']};"
            f" selection-background-color: {self.theme['selection']}; }}")

        index = self.tabs_widget.addTab(area, os.path.basename(path))
        self.tabs_widget.setTabToolTip(index, path)
        self.tabs[path] = area
        self._note_recent(path)
        self.tabs_widget.setCurrentIndex(index)

        if self.welcome in [self.tabs_widget.widget(i) for i in range(self.tabs_widget.count())]:
            welcome_index = self.tabs_widget.indexOf(self.welcome)
            if welcome_index >= 0:
                self.tabs_widget.removeTab(welcome_index)

        self.path_label.setText(path)
        self.project_panel.select(path)

        if hasattr(self, "server"):
            self.server.did_open(uri_for(path), editor.toPlainText())
            self._request_outline(path)

    def show_diff(self, path, staged=False):
        """Opens (or refreshes) a diff tab for one file."""
        repository = self.changes_panel.repository
        if repository is None:
            return

        key = ("staged:" if staged else "work:") + os.path.abspath(path)
        text = repository.diff(path, staged=staged)
        lines = parse_diff(text)
        label = os.path.basename(path) + (" (staged)" if staged else "") + " — diff"

        if not lines:
            lines = [("meta", "No changes in this file.")]

        if key in self.diff_tabs:
            panel = self.diff_tabs[key]
            panel.set_diff(label, lines)
            self.tabs_widget.setCurrentWidget(panel)
            return

        panel = DiffPanel(self.theme)
        panel.set_diff(label, lines)
        self.diff_tabs[key] = panel
        index = self.tabs_widget.addTab(panel, label)
        self.tabs_widget.setTabToolTip(index, path)
        self.tabs_widget.setCurrentWidget(panel)

    def _diff_selected(self):
        """Shows the diff for the file in the editor, or the selected change."""
        if self.changes_dock.isVisible() and self.changes_panel.selected_paths():
            paths = self.changes_panel.selected_paths()
            for path in paths[:1]:
                self.show_diff(path)
            return

        editor = self._editor()
        if editor is not None:
            self.show_diff(editor.path)

    def refresh_vcs(self):
        self.changes_panel.refresh()
        for key, panel in list(self.diff_tabs.items()):
            path = key.split(":", 1)[1]
            staged = key.startswith("staged:")
            if self.changes_panel.repository is None:
                continue
            lines = parse_diff(self.changes_panel.repository.diff(path, staged=staged))
            panel.set_diff(os.path.basename(path) + (" (staged)" if staged else "") + " — diff",
                           lines or [("meta", "No changes in this file.")])
        self.status_branch.setText(self.changes_panel.repository.branch()
                                   if self.changes_panel.repository else "")

    def _on_committed(self, message):
        first = message.splitlines()[0] if message.strip() else ""
        self.console.write(f"Committed: {first}\n", info=True)
        self.refresh_vcs()

    def open_file_dialog(self):
        path, _ = QFileDialog.getOpenFileName(self, "Open TERN file", self.root, "TERN files (*.tern);;All files (*)")
        if path:
            self.open_file(path)

    def new_file(self):
        path, _ = QFileDialog.getSaveFileName(self, "New TERN file", os.path.join(self.root, "untitled.tern"),
                                              "TERN files (*.tern)")
        if not path:
            return
        with open(path, "w", encoding="utf-8") as handle:
            handle.write('import "std:json";\n\nfn main() {\n    println("hello");\n}\n\nmain();\n')
        self.open_file(path)

    def save_current(self):
        editor = self._editor()
        if editor is None:
            return
        try:
            with open(editor.path, "w", encoding="utf-8") as handle:
                handle.write(editor.toPlainText())
            editor.document().setModified(False)
            self._tab_changed()
            if hasattr(self, "changes_panel"):
                self.changes_panel.refresh()
            self.console.set_status(f"saved {os.path.basename(editor.path)}")
        except OSError as error:
            QMessageBox.warning(self, "TERN Studio", f"Could not save:\n{error}")

    def save_all(self):
        for path in list(self.tabs):
            editor = self.tabs[path].editor
            if editor.document().isModified():
                try:
                    with open(path, "w", encoding="utf-8") as handle:
                        handle.write(editor.toPlainText())
                    editor.document().setModified(False)
                except OSError:
                    pass
        self._tab_changed()

    def close_tab(self, index):
        widget = self.tabs_widget.widget(index)
        if isinstance(widget, EditorArea):
            path = widget.editor.path
            if widget.editor.document().isModified():
                answer = QMessageBox.question(
                    self, "TERN Studio", f"Save changes to {os.path.basename(path)}?",
                    QMessageBox.Save | QMessageBox.Discard | QMessageBox.Cancel)
                if answer == QMessageBox.Cancel:
                    return
                if answer == QMessageBox.Save:
                    self.save_current()

            if hasattr(self, "server"):
                self.server.did_close(uri_for(path))
            self.tabs.pop(path, None)
        else:
            for key, panel in list(self.diff_tabs.items()):
                if panel is widget:
                    del self.diff_tabs[key]

        self.tabs_widget.removeTab(index)
        if self.tabs_widget.count() == 0:
            self.tabs_widget.addTab(self.welcome, "Welcome")

    def _tab_changed(self, *_):
        area = self._area()
        if area is None:
            # A diff view, or the window still being built: no symbols, no caret.
            if hasattr(self, "structure_panel"):
                self.structure_panel.set_symbols([], lambda line: 0)
            index = self.tabs_widget.currentIndex()
            if index >= 0:
                self.tabs_widget.setTabText(index, self.tabs_widget.tabText(index).lstrip("*"))
            return
        if area is not None:
            index = self.tabs_widget.indexOf(area)
            name = os.path.basename(area.editor.path)
            mark = "*" if area.editor.document().isModified() else ""
            self.tabs_widget.setTabText(index, mark + name)
            self.path_label.setText(area.editor.path)

    def _cursor_moved(self, line, column):
        self.status_position.setText(f"Ln {line}, Col {column}")

    def _goto_offset(self, offset):
        editor = self._editor()
        if editor is None:
            return
        cursor = editor.textCursor()
        cursor.setPosition(min(offset, len(editor.toPlainText())))
        editor.setTextCursor(cursor)
        editor.centerCursor()
        editor.setFocus()

    def _goto_path(self, path, line, character):
        self.open_file(path)
        editor = self._editor()
        if editor is not None:
            editor.goto(line, character)
            editor.setFocus()

    # ------------------------------------------------------------ diagnostics

    @staticmethod
    def _is_tern(path):
        """True for a TERN document: the only kind the syntax server analyses."""
        return str(path).endswith(".tern")

    def _on_diagnostics(self, uri, diagnostics):
        path = path_for(uri)
        area = self.tabs.get(path)
        if area is None:
            return

        if not self._is_tern(path):
            # The server only speaks TERN. A Markdown tab gets no underlines,
            # and the Problems window keeps whatever the scripts said.
            area.editor.set_problems([])
            self._refresh_problems()
            return

        problems = []
        for diagnostic in diagnostics:
            line = diagnostic.get("range", {}).get("start", {}).get("line", 0)
            character = diagnostic.get("range", {}).get("start", {}).get("character", 0)
            problems.append((diagnostic.get("severity", 1), line, diagnostic.get("message", ""),
                             character, diagnostic.get("code", "")))

        area.editor.set_problems([(severity, line, message) for severity, line, message, _c, _code in problems])
        self._refresh_problems()

    def _refresh_problems(self):
        entries = []
        for path, area in self.tabs.items():
            for severity, line, message, character, code in getattr(area, "_problems", []):
                entries.append((severity, path, line, character, message, code))

        # Read back from the editor, which is the single place they live.
        entries = []
        for path, area in self.tabs.items():
            for severity, line, message in area.editor.problems:
                entries.append((severity, path, line, 0, message, ""))

        self.problems_panel.set_problems(entries)
        errors = sum(1 for entry in entries if entry[0] == 1)
        warnings = len(entries) - errors
        text = "No problems" if not entries else f"{errors} error(s), {warnings} warning(s)"
        self.status_problems.setText(text)

    def _request_outline(self, path):
        uri = uri_for(path)
        self.server.document_symbols(uri, lambda symbols: self._outline_arrived(path, symbols))

    def _outline_arrived(self, path, symbols):
        self._outline_cache[path] = list(symbols or [])
        area = self.tabs.get(path)
        if area is None or area is not self._area():
            return

        editor = area.editor
        document = editor.document()

        def offset_for_line(line):
            block = document.findBlockByNumber(line)
            return block.position() if block.isValid() else 0

        self.structure_panel.set_symbols(symbols or [], offset_for_line)

    # ------------------------------------------------------------- completion

    def _completion(self, editor, offset):
        if not hasattr(self, "server"):
            return
        uri = uri_for(editor.path)
        line, character = self._position(editor, offset)
        self.server.complete(uri, line, character, lambda result: self._completion_arrived(editor, result, offset))

    def _completion_arrived(self, editor, result, offset):
        if result is None:
            return
        items = result.get("items", []) if isinstance(result, dict) else result
        editor.set_completions(items, offset)

    def complete(self):
        editor = self._editor()
        if editor is None:
            return
        editor.request_completion()

    @staticmethod
    def _position(editor, offset):
        cursor = editor.textCursor()
        cursor.setPosition(min(offset, len(editor.toPlainText())))
        block = cursor.block()
        return block.blockNumber(), cursor.positionInBlock()

    # ------------------------------------------------------------------ hover

    def _hover(self, editor, point):
        if not hasattr(self, "server"):
            return

        cursor = editor.cursorForPosition(editor.viewport().mapFromGlobal(point))
        line = cursor.blockNumber()
        character = cursor.positionInBlock()

        if self.pending_hover == (editor, (line, character)):
            return
        self.pending_hover = (editor, (line, character))

        self.server.hover(uri_for(editor.path), line, character,
                          lambda result: self._hover_arrived(editor, point, result))

    def _hover_arrived(self, editor, point, result):
        if result is None:
            return
        contents = result.get("contents")
        if isinstance(contents, dict):
            contents = contents.get("value", "")
        if not contents:
            return
        if contents.startswith("```"):
            contents = contents.replace("```tern", "").replace("```", "").strip()
        editor.show_hover(point, contents)

    # ------------------------------------------------------------- navigation

    def goto_definition(self):
        editor = self._editor()
        if editor is None:
            return
        cursor = editor.textCursor()
        uri = uri_for(editor.path)
        line, character = cursor.blockNumber(), cursor.positionInBlock()

        self.server.definition(uri, line, character, lambda result: self._definition_arrived(result))

    def _definition_arrived(self, result):
        if not result:
            self.console.write("No definition found at the cursor.\n", info=True)
            return
        if isinstance(result, list):
            result = result[0] if result else None
        if not result:
            return
        path = path_for(result.get("uri", ""))
        line = result.get("range", {}).get("start", {}).get("line", 0)
        character = result.get("range", {}).get("start", {}).get("character", 0)
        self._goto_path(path, line, character)

    def find_usages(self):
        editor = self._editor()
        if editor is None:
            return
        cursor = editor.textCursor()
        uri = uri_for(editor.path)
        line, character = cursor.blockNumber(), cursor.positionInBlock()
        word = cursor.selectedText() or ""
        self.server.references(uri, line, character, lambda result: self._usages_arrived(editor, word, result))

    def _usages_arrived(self, editor, word, result):
        if not result:
            self.console.write("No usages found.\n", info=True)
            return

        self.console.clear()
        self.console.write(f"{len(result)} usage(s):\n", info=True)
        for entry in result:
            path = path_for(entry.get("uri", ""))
            line = entry.get("range", {}).get("start", {}).get("line", 0) + 1
            self.console.write(f"  {os.path.basename(path)}:{line}\n")

        self._show_dock(self.console_dock)

    def rename(self):
        editor = self._editor()
        if editor is None:
            return
        cursor = editor.textCursor()
        if not cursor.selectedText():
            cursor.select(cursor.WordUnderCursor)
        current = cursor.selectedText()

        new_name, ok = QInputDialog.getText(self, "Rename", "New name:", text=current)
        if not ok or not new_name or new_name == current:
            return

        position = editor.textCursor()
        line = position.blockNumber()
        character = position.positionInBlock()
        uri = uri_for(editor.path)

        self.server.rename(uri, line, character, new_name,
                           lambda result: self._rename_arrived(editor, new_name, result))

    def _rename_arrived(self, editor, new_name, result):
        if not result:
            self.console.write("Rename was refused by the syntax server (see the log).\n", error=True)
            self._show_dock(self.console_dock)
            return

        changes = result.get("changes", {})
        for uri, edits in changes.items():
            path = path_for(uri)
            self.open_file(path)
            area = self.tabs.get(path)
            if area is None:
                continue
            target = area.editor
            text = target.toPlainText()
            # Apply from the end so earlier offsets stay valid.
            for edit in sorted(edits, key=lambda e: self._offset(area.editor, e["range"]), reverse=True):
                start = self._offset(target, edit["range"])
                end = self._offset(target, edit["range"], end=True)
                text = text[:start] + edit["newText"] + text[end:]
            cursor_position = target.textCursor().position()
            target.setPlainText(text)
            cursor = target.textCursor()
            cursor.setPosition(min(cursor_position, len(text)))
            target.setTextCursor(cursor)
            if hasattr(self, "server"):
                self.server.did_change(uri_for(path), text)

        self.console.set_status(f"renamed to {new_name}")

    @staticmethod
    def _offset(editor, range_, end=False):
        position = range_["end"] if end else range_["start"]
        block = editor.document().findBlockByNumber(position.get("line", 0))
        return block.position() + position.get("character", 0) if block.isValid() else 0

    # ----------------------------------------------------------------- format

    def format_code(self):
        editor = self._editor()
        if editor is None:
            return
        self.server.format_document(uri_for(editor.path),
                                    lambda result: self._format_arrived(editor, result))

    def _format_arrived(self, editor, result):
        if not result:
            return
        text = editor.toPlainText()
        for edit in sorted(result, key=lambda e: self._offset(editor, e["range"]), reverse=True):
            start = self._offset(editor, edit["range"])
            end = self._offset(editor, edit["range"], end=True)
            text = text[:start] + edit["newText"] + text[end:]

        position = editor.textCursor().position()
        editor.setPlainText(text if not text.endswith("\n") else text[:-1])
        cursor = editor.textCursor()
        cursor.setPosition(min(position, len(text)))
        editor.setTextCursor(cursor)
        if hasattr(self, "server"):
            self.server.did_change(uri_for(editor.path), editor.toPlainText())

    # ----------------------------------------------------------------- search

    def search_everywhere(self):
        dialog = SearchEverywhereDialog(self.theme, self)
        dialog.textEdited.connect(lambda text: self._search_symbols(dialog, text))
        dialog.chosen.connect(self._goto_path)
        self._search_symbols(dialog, "")
        dialog.show()

    def _search_symbols(self, dialog, text):
        self.server.workspace_symbols(text, lambda result: self._search_arrived(dialog, result))

    def _search_arrived(self, dialog, result):
        entries = []
        for symbol in result or []:
            location = symbol.get("location", {})
            path = path_for(location.get("uri", ""))
            line = location.get("range", {}).get("start", {}).get("line", 0)
            character = location.get("range", {}).get("start", {}).get("character", 0)
            entries.append((symbol.get("name", ""), os.path.basename(path), path, line, character))
        dialog.show_results(entries)

    def goto_file(self):
        pattern, ok = QInputDialog.getText(self, "Go to File", "File name:")
        if not ok or not pattern:
            return
        matches = []
        for directory, subdirs, names in os.walk(self.root):
            subdirs[:] = [d for d in subdirs if d not in (".git", "build", "node_modules", "obj", "bin")]
            for name in names:
                if name.endswith(".tern") and pattern.lower() in name.lower():
                    matches.append(os.path.join(directory, name))
        if matches:
            self.open_file(matches[0])
        else:
            self.console.write(f"No file matching '{pattern}'.\n", info=True)

    def goto_symbol(self):
        self.search_everywhere()

    def show_find_in_path(self, term=None):
        """Open the Find window, seeded with the editor's selection."""
        self._show_dock(self.find_dock)
        self.find_dock.raise_()
        if term is None:
            editor = self._editor()
            if editor is not None:
                term = editor.textCursor().selectedText().split("\u2029")[0]
        if term:
            self.find_panel.query.setText(term)
        self.find_panel.query.setFocus()
        self.find_panel.query.selectAll()
        if self.find_panel.query.text():
            self.find_panel.run_search()
        else:
            self.find_panel.results.clear()
            self.find_panel.summary.setText("Type to search")

    def goto_symbol_in_file(self):
        """A filtered list of the open file's declarations, as Ctrl+F12 does."""
        editor = self._editor()
        if editor is None:
            return

        # Ask the server again: the cache may predate the last edit.
        self.server.document_symbols(
            uri_for(editor.path),
            lambda symbols: self._symbol_dialog(editor, symbols or []))

    def _symbol_dialog(self, editor, symbols):
        if not symbols:
            self.console.write("No symbols in this file.\n", info=True)
            return

        labels = []
        for symbol in symbols:
            line = symbol.get("range", {}).get("start", {}).get("line", 0) + 1
            labels.append(f"{symbol.get('name', '?')}  —  line {line}")

        dialog = SymbolDialog(labels, self.theme, self)
        if dialog.exec() == dialog.Accepted and dialog.chosen is not None:
            line = symbols[dialog.chosen].get("range", {}).get("start", {}).get("line", 0)
            editor.goto(line, 0)
            editor.setFocus()

    def _goto_line_column(self, path, line, column):
        self.open_file(path)
        editor = self._editor()
        if editor is None:
            return
        editor.goto(line - 1, column)
        editor.setFocus()

    def show_keymap(self):
        """The shortcuts, in one dialog, because a menu is not a reference."""
        rows = []
        for name, action in sorted(self.actions_map.items(), key=lambda pair: pair[1].text()):
            shortcut = action.shortcut().toString()
            if shortcut:
                rows.append(f"{shortcut:<22}{action.text()}")
        text = "\n".join(rows)
        dialog = QMessageBox(self)
        dialog.setWindowTitle("Default Keymap")
        dialog.setText("Shortcuts in effect")
        dialog.setDetailedText(text)
        dialog.setStandardButtons(QMessageBox.Ok)
        dialog.exec()

    def find(self, with_replace=False):
        """Opens the find bar on the current editor, seeded with the selection."""
        area = self._area()
        if area is None:
            return
        area.find_bar.open(with_replace=with_replace)

    def find_next(self):
        area = self._area()
        if area is not None:
            if area.find_bar.isVisible() and area.find_bar.find_field.text():
                area.find_bar.find_next()
            else:
                area.find_bar.open()

    def find_previous(self):
        area = self._area()
        if area is not None:
            if area.find_bar.isVisible() and area.find_bar.find_field.text():
                area.find_bar.find_previous()
            else:
                area.find_bar.open()

    def toggle_comment(self):
        editor = self._editor()
        if editor is not None:
            editor.toggle_comment()

    def duplicate_line(self):
        editor = self._editor()
        if editor is not None:
            editor.duplicate_line()

    def move_lines(self, delta):
        editor = self._editor()
        if editor is not None:
            editor.move_lines(delta)

    def goto_line(self):
        editor = self._editor()
        if editor is not None:
            total = editor.blockCount()
            number, ok = QInputDialog.getInt(self, "Go to Line", f"Line (1-{total}):",
                                             editor.textCursor().blockNumber() + 1, 1, total)
            if ok:
                editor.goto(number - 1, 0)
                editor.setFocus()

    def recent_files(self):
        """The files this session has had open, most recent first."""
        paths = [path for path in self._recent if os.path.exists(path)]
        paths += [path for path in self.tabs if path not in paths]
        if not paths:
            self.console.write("No files opened yet.\n", info=True)
            return

        dialog = RecentFilesDialog(paths, self.theme, self)
        if dialog.exec() == dialog.Accepted and dialog.chosen:
            self.open_file(dialog.chosen)

    def _note_recent(self, path):
        if path in self._recent:
            self._recent.remove(path)
        self._recent.insert(0, path)
        del self._recent[30:]

    # --------------------------------------------------------------- debugging

    def toggle_breakpoint(self):
        """Sets or clears a breakpoint on the line the caret is on."""
        editor = self._editor()
        if editor is None:
            return
        line0 = editor.textCursor().blockNumber()
        present = editor.toggle_breakpoint(line0)
        self.status_problems.setText(
            f"Breakpoint {'set' if present else 'removed'} at line {line0 + 1}")
        if self.debug_session is not None and self.debug_session.running:
            self.debug_session.set_breakpoints(sorted(editor.breakpoints))

    def debug_current(self):
        """Starts the debugger on the current file, if it is not already going."""
        editor = self._editor()
        if editor is None:
            return

        if self.debug_session is not None and self.debug_session.running:
            self.debug_session.stop()
            self.debug_session = None

        if editor.document().isModified():
            self.save_current()

        # Every open file's breakpoints go with the session, so a breakpoint set
        # in a helper file the script imports is honoured too.
        breakpoints = sorted(editor.breakpoints)
        session = DebugSession(editor.path, breakpoints, self.install, self)
        session.started.connect(lambda: self._debug_event("Debugger attached", None))
        session.stopped.connect(self._debug_stopped)
        session.output.connect(lambda text: self.console.write(text))
        session.exited.connect(self._debug_exited)
        session.failed.connect(self._debug_failed)
        session.server_log.connect(lambda text: self.console.write(f"[debug] {text}\n", info=True))

        if not session.start():
            self.debug_session = None
            return

        self.debug_session = session
        self.debug_panel.set_running()
        self._show_dock(self.debug_dock)

    def _debug_event(self, message, _payload):
        self.debug_panel.state.setText(message)

    def _debug_stopped(self, message):
        frames = message.get("frames", [])
        line = message.get("line", 0)
        reason = message.get("reason", "breakpoint")
        self.debug_panel.set_stopped(f"Paused on {reason} at line {line}", frames)

        editor = self._editor()
        if editor is not None:
            editor.set_stopped_line(line)
        self._show_dock(self.debug_dock)
        self._show_frame_variables(self.debug_panel.current_frame() or 0)

    def _show_frame_variables(self, frame):
        if self.debug_session is None:
            return
        index = 0 if frame is None else int(frame)
        self.debug_session.variables(
            index, lambda response: self.debug_panel.set_variables(response.get("variables", [])))

    def _evaluate_expression(self, expression):
        if self.debug_session is None or not expression.strip():
            return
        frame = self.debug_panel.current_frame() or 0
        self.debug_session.evaluate(
            expression, lambda response: self.debug_panel.set_evaluated(
                f"{expression} = {response.get('value', '?')}"))

    def _debug_exited(self, ok, message):
        if ok:
            self.debug_panel.set_idle("Program finished")
            self.console.write("[debug] exited\n", info=True)
        else:
            self.debug_panel.set_idle(f"Stopped: {message}")
            self.console.write(f"[debug] {message}\n", error=True)
        editor = self._editor()
        if editor is not None:
            editor.set_stopped_line(-1)
        self.debug_session = None

    def _debug_failed(self, message):
        self.debug_panel.set_idle("Not debugging")
        self.console.write(f"[debug] {message}\n", error=True)
        self.debug_session = None

    def _debug_command(self, name):
        if self.debug_session is None or not self.debug_session.running:
            return False
        getattr(self.debug_session, name)()
        self.debug_panel.state.setText("Running")
        return True

    def debug_resume(self):
        self._debug_command("resume")

    def debug_step_over(self):
        self._debug_command("step_over")

    def debug_step_into(self):
        self._debug_command("step_into")

    def debug_step_out(self):
        self._debug_command("step_out")

    # -------------------------------------------------------------------- run

    def run_current(self):
        editor = self._editor()
        if editor is None:
            return
        if editor.document().isModified():
            self.save_current()
        self._start_process([self.launcher, "run", editor.path], "Run")

    def compile_and_run(self):
        editor = self._editor()
        if editor is None:
            return
        if editor.document().isModified():
            self.save_current()

        output = os.path.join(self.root, "build", "studio", os.path.splitext(os.path.basename(editor.path))[0])
        os.makedirs(os.path.dirname(output), exist_ok=True)
        self.console.clear()
        self.console.write(f"Compiling {os.path.basename(editor.path)} with the native backend...\n", info=True)
        self._show_dock(self.console_dock)

        compiler = QProcess(self)
        compiler.setWorkingDirectory(self.root)
        compiler.setProcessChannelMode(QProcess.MergedChannels)
        compiler.readyReadStandardOutput.connect(
            lambda: self.console.write(bytes(compiler.readAllStandardOutput()).decode("utf-8", "replace")))
        compiler.finished.connect(lambda code, _status: self._compiled(code, output))
        compiler.start(sys.executable, [os.path.join(self.root, "native", "ternc.py"), editor.path, "-o", output])

    def _compiled(self, code, output):
        if code != 0:
            self.console.write(f"\nCompilation failed (exit {code}).\n", error=True)
            self.console.set_status("compile failed")
            return
        self.console.write("\nRunning the compiled binary:\n\n", info=True)
        self._start_process([output], "Compiled", append=True)

    def _start_process(self, command, name, append=False):
        self.stop_run()
        if not append:
            self.console.clear()
            self.console.write(f"$ {' '.join(command)}\n\n", info=True)

        self._show_dock(self.console_dock)
        self.console.set_status(f"{name}: running")

        process = QProcess(self)
        process.setWorkingDirectory(self.root)
        process.setProcessChannelMode(QProcess.MergedChannels)
        process.readyReadStandardOutput.connect(
            lambda: self.console.write(bytes(process.readAllStandardOutput()).decode("utf-8", "replace")))
        process.finished.connect(lambda code, _status: self._process_finished(name, code))
        process.start(command[0], command[1:])
        self.run_process = process

    def _process_finished(self, name, code):
        self.console.write(f"\n[{name} exited with {code}]\n", info=code == 0, error=code != 0)
        self.console.set_status(f"{name}: exited with {code}")
        self.run_process = None

    def stop_run(self):
        if self.run_process is not None and self.run_process.state() != QProcess.NotRunning:
            self.run_process.kill()
            self.run_process = None

    def check_file(self):
        editor = self._editor()
        if editor is None:
            return
        if editor.document().isModified():
            self.save_current()
        self._start_process([self.launcher, "check", editor.path], "Check")

    # ------------------------------------------------------------------ misc

    def _show_dock(self, dock):
        dock.show()
        dock.raise_()

    def set_theme(self, name):
        self.theme_name = name
        self.theme = THEMES[name]
        self.actions_map["theme_darcula"].setChecked(name == "darcula")
        self.actions_map["theme_light"].setChecked(name == "light")
        self._apply_theme()

    def changeEvent(self, event):
        # Coming back from another window is a good moment to re-read git.
        super().changeEvent(event)
        if event.type() == QEvent.ActivationChange and self.isActiveWindow():
            if hasattr(self, "changes_panel"):
                self.refresh_vcs()

    def showEvent(self, event):
        super().showEvent(event)
        if not getattr(self, "_docks_sized", False):
            self._docks_sized = True
            QTimer.singleShot(0, self._size_docks)

    def _size_docks(self):
        self.resizeDocks([self.console_dock, self.problems_dock, self.terminal_dock],
                         [180, 180, 180], Qt.Vertical)
        self.resizeDocks([self.project_dock, self.structure_dock], [380, 180], Qt.Vertical)

    def _apply_theme(self):
        app = QApplication.instance()
        if app is not None:
            app.setStyleSheet(stylesheet(self.theme))

        for panel in (self.project_panel, self.structure_panel, self.problems_panel,
                      self.console, self.terminal, self.changes_panel, self.debug_panel,
                      self.find_panel):
            panel.apply_theme(self.theme)

        for area in self.tabs.values():
            area.apply_theme(self.theme)
            area.editor.setStyleSheet(
                f"QPlainTextEdit {{ background: {self.theme['bg']}; color: {self.theme['fg']};"
                f" selection-background-color: {self.theme['selection']}; }}")

        self.welcome.setStyleSheet(
            f"background: {self.theme['bg']}; color: {self.theme['fg']};")
        self.path_label.setStyleSheet(f"color: {self.theme['fg_dim']}; padding-right: 8px;")

    def open_docs(self):
        path = os.path.join(self.root, "docs", "msdn", "README.md")
        if os.path.exists(path):
            self.open_file(path)
        else:
            self.console.write("docs/msdn/README.md is missing; run tools/gen-docs.sh.\n", info=True)

    def about(self):
        QMessageBox.about(
            self, "TERN Studio",
            "<h3>TERN Studio</h3>"
            "<p>An IDE for the TERN language, shaped after JetBrains Rider.</p>"
            "<p>The editor is driven by <code>tern-ls</code>, a Language Server Protocol "
            "implementation built from the same parser and analyser as the compiler.</p>"
            "<p>Syntax server: " + self.status_server.text() + "</p>")

    def _restore(self):
        """Puts back what the last session was looking at."""
        if self.settings is None:
            return

        theme = self.settings.value("theme", self.theme_name, str)
        if theme in THEMES and theme != self.theme_name:
            self.set_theme(theme)

        geometry = self.settings.value("geometry")
        if geometry is not None:
            self.restoreGeometry(geometry)

        state = self.settings.value("state")
        if state is not None:
            self.restoreState(state)

    def _remember(self):
        if self.settings is None:
            return

        self.settings.setValue("theme", self.theme_name)
        self.settings.setValue("geometry", self.saveGeometry())
        self.settings.setValue("state", self.saveState())
        self.settings.setValue("project", self.root)
        open_files = [path for path in self.tabs]
        self.settings.setValue("open_files", open_files)
        current = self.tabs_widget.currentWidget()
        if current is not None:
            for path, area in self.tabs.items():
                if area is current:
                    self.settings.setValue("current_file", path)
                    break
        self.settings.sync()

    def _shutdown(self):
        if self.debug_session is not None:
            self.debug_session.stop()
            self.debug_session = None
        if hasattr(self, "server"):
            self.server.stop()
        self.stop_run()

    def closeEvent(self, event):
        for path in list(self.tabs):
            if self.tabs[path].editor.document().isModified():
                self.tabs_widget.setCurrentWidget(self.tabs[path])
                answer = QMessageBox.question(
                    self, "TERN Studio", f"Save changes to {os.path.basename(path)}?",
                    QMessageBox.Save | QMessageBox.Discard | QMessageBox.Cancel)
                if answer == QMessageBox.Cancel:
                    event.ignore()
                    return
                if answer == QMessageBox.Save:
                    self.save_current()

        if hasattr(self, "server"):
            self.server.stop()
        self._remember()
        super().closeEvent(event)
