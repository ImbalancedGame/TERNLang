"""Git, as much of it as an IDE panel needs.

Everything here shells out to `git` and parses what it prints. That is
deliberate: there is no libgit2 binding worth the dependency, git is already
installed wherever a repository is, and the porcelain formats used below are
stable and documented.

Every call is read-only unless the name says otherwise (`stage`, `unstage`,
`commit`), so opening a repository in the IDE cannot alter it. When git is
missing, or the directory is not a repository, `Repository.open` returns None
and the panel says so instead of failing.
"""

import os
import shutil
import subprocess


class ChangedFile:
    """One entry of `git status`, in the shape the panel draws."""

    def __init__(self, path, index_status, worktree_status):
        self.path = path
        self.index_status = index_status
        self.worktree_status = worktree_status

    @property
    def staged(self):
        return self.index_status not in (" ", "?", "!")

    @property
    def untracked(self):
        return self.index_status == "?" or self.worktree_status == "?"

    @property
    def letter(self):
        """The single character git's GUI clients show for this file."""
        if self.untracked:
            return "?"
        if self.worktree_status not in (" ", "?"):
            return self.worktree_status
        return self.index_status

    @property
    def kind(self):
        return {
            "M": "modified", "A": "added", "D": "deleted", "R": "renamed",
            "C": "copied", "U": "conflicted", "T": "type changed", "?": "untracked",
        }.get(self.letter, "changed")

    def __repr__(self):
        return f"ChangedFile({self.path!r}, {self.index_status}{self.worktree_status})"


class Repository:
    def __init__(self, root):
        self.root = root

    # ------------------------------------------------------------------ open

    @staticmethod
    def open(path):
        """Finds the repository containing `path`, or returns None."""
        if shutil.which("git") is None:
            return None
        top = Repository._run(path, "rev-parse", "--show-toplevel")
        if top is None:
            return None
        return Repository(top.strip())

    # ---------------------------------------------------------------- plumbing

    @staticmethod
    def _run(cwd, *arguments, timeout=20):
        """Runs git and returns stdout, or None when it fails."""
        try:
            result = subprocess.run(
                ["git", *arguments], cwd=cwd, capture_output=True, text=True,
                timeout=timeout, stdin=subprocess.DEVNULL,
                env={**os.environ, "GIT_TERMINAL_PROMPT": "0",
                     "GIT_OPTIONAL_LOCKS": "0"})
        except (OSError, subprocess.SubprocessError):
            return None
        if result.returncode != 0:
            return None
        return result.stdout

    def git(self, *arguments, **keyword):
        return self._run(self.root, *arguments, **keyword)

    # ---------------------------------------------------------------- reading

    def branch(self):
        """The current branch, or the short hash when detached."""
        name = self.git("rev-parse", "--abbrev-ref", "HEAD")
        if name is None:
            return ""
        name = name.strip()
        if name == "HEAD":
            short = self.git("rev-parse", "--short", "HEAD")
            return f"detached at {short.strip()}" if short else "detached"
        return name

    def upstream_ahead_behind(self):
        """(ahead, behind) against the upstream, or None when there is none."""
        counts = self.git("rev-list", "--left-right", "--count", "@{upstream}...HEAD")
        if counts is None:
            return None
        parts = counts.split()
        if len(parts) != 2:
            return None
        try:
            behind, ahead = int(parts[0]), int(parts[1])
        except ValueError:
            return None
        return ahead, behind

    def status(self):
        """Every changed file, staged or not, in path order.

        `--porcelain=v1 -z` is used because it is unambiguous about renames and
        about paths with spaces, both of which matter for a file list.
        """
        output = self.git("status", "--porcelain=v1", "-z", "--untracked-files=all")
        if output is None:
            return []

        entries = output.split("\0")
        files = []
        index = 0
        while index < len(entries):
            entry = entries[index]
            index += 1
            if len(entry) < 4:
                continue
            index_status, worktree_status = entry[0], entry[1]
            path = entry[3:]
            if index_status in ("R", "C"):
                # A rename/copy is followed by the original path in its own
                # record; the new path is the one worth showing.
                index += 1
            files.append(ChangedFile(path, index_status, worktree_status))

        return sorted(files, key=lambda item: item.path)

    def head_for(self, path):
        """The file's committed text, or None when it is new."""
        return self.git("show", f"HEAD:{_relative(self.root, path)}")

    def diff(self, path=None, staged=False):
        """A unified diff with three lines of context, as git writes it."""
        arguments = ["diff", "--no-color", "--unified=3"]
        if staged:
            arguments.append("--cached")
        if path is not None:
            arguments += ["--", _relative(self.root, path)]
        output = self.git(*arguments)
        if output is None:
            return ""

        if path is not None and not output and self.is_untracked(path):
            # An untracked file has no diff; show its contents as additions so
            # the panel has something to display.
            try:
                with open(path, "r", encoding="utf-8", errors="replace") as handle:
                    text = handle.read()
            except OSError:
                return ""
            header = f"diff --git a/{_relative(self.root, path)} b/{_relative(self.root, path)}\n"
            body = "".join(f"+{line}" for line in text.splitlines(keepends=True))
            return header + "--- /dev/null\n+++ b/" + _relative(self.root, path) + "\n" + body
        return output

    def is_untracked(self, path):
        listing = self.git("ls-files", "--others", "--exclude-standard", "--",
                           _relative(self.root, path))
        return bool(listing and listing.strip())

    def log(self, count=20):
        """Recent commits as (hash, subject) pairs."""
        output = self.git("log", f"-{count}", "--pretty=format:%h%x00%s", "--")
        if not output:
            return []
        commits = []
        for line in output.splitlines():
            if "\0" in line:
                revision, subject = line.split("\0", 1)
                commits.append((revision, subject))
        return commits

    # ---------------------------------------------------------------- writing

    def stage(self, paths):
        if not paths:
            return True
        return self.git("add", "--", *paths) is not None

    def unstage(self, paths):
        if not paths:
            return True
        # `restore --staged` needs git 2.23; `reset` is older but works
        # everywhere and says the same thing.
        return self.git("reset", "-q", "HEAD", "--", *paths) is not None

    def commit(self, message):
        """Commits the staged changes. Returns (ok, output)."""
        if not message.strip():
            return False, "the commit message is empty"
        result = subprocess.run(
            ["git", "commit", "-m", message], cwd=self.root, capture_output=True,
            text=True, timeout=60, stdin=subprocess.DEVNULL,
            env={**os.environ, "GIT_TERMINAL_PROMPT": "0"})
        output = (result.stdout + result.stderr).strip()
        return result.returncode == 0, output


def _relative(root, path):
    """The path as git knows it: relative to the repository root."""
    try:
        return os.path.relpath(os.path.abspath(path), root).replace(os.sep, "/")
    except ValueError:
        return path


def parse_diff(text):
    """Turns a unified diff into (kind, text) lines for display.

    `kind` is one of "meta", "hunk", "add", "remove" or "context", which is all
    the colouring the diff view needs.
    """
    lines = []
    for line in text.splitlines():
        if line.startswith("+++") or line.startswith("---") or line.startswith("diff "):
            lines.append(("meta", line))
        elif line.startswith("@@"):
            lines.append(("hunk", line))
        elif line.startswith("+"):
            lines.append(("add", line))
        elif line.startswith("-"):
            lines.append(("remove", line))
        else:
            lines.append(("context", line))
    return lines
