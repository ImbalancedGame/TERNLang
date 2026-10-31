#!/usr/bin/env python3
"""Checks on the source itself, which no run of the program would catch.

Two kinds of defect live here, both of which have shipped:

* a string with an escape the language does not know. Python 3.12 turned these
  from silence into `SyntaxWarning: invalid escape sequence`, and 3.13 prints
  them on every launch, which is how `tools/studio.py` came to start with a
  wall of text about a docstring describing `tools[.]venv`. A docstring is
  still a string literal; a Windows path in one needs an `r` prefix.

* a file that does not parse at all under the interpreter running the suite.
  That is usually a `tools/*.py` no test imports, so nothing else would say so
  until a user ran it.

    python3 tools/test_sources.py            run it
    python3 tools/test_sources.py -v         show each check
"""

import os
import sys
import tokenize

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SKIP = {'.venv', 'build', 'dist', '__pycache__', '.git', 'node_modules'}

BACKSLASH = chr(92)
DOUBLE = chr(34)
SINGLE = chr(39)
TRIPLE_DOUBLE = DOUBLE * 3
TRIPLE_SINGLE = SINGLE * 3

# The escapes Python defines. Anything else after a backslash is the warning.
KNOWN = set(BACKSLASH + SINGLE + DOUBLE + 'abfnrtv01234567xNuU\n')


class Checks:
    def __init__(self, verbose):
        self.verbose = verbose
        self.passed = 0
        self.failed = []

    def check(self, name, condition, detail=''):
        if condition:
            self.passed += 1
            if self.verbose:
                print(f'  ok   {name}')
        else:
            self.failed.append(name)
            print(f'  FAIL {name}')
            if detail:
                print(f'       {detail}')


def sources():
    """Every Python file that belongs to the repository."""
    for folder, names, files in os.walk(ROOT):
        names[:] = sorted(name for name in names if name not in SKIP)
        for name in sorted(files):
            if name.endswith('.py'):
                yield os.path.join(folder, name)


def string_issues(path):
    """`(line, kind, snippet)` for every string literal worth complaining about.

    `kind` is `escape` for a backslash that starts an escape Python does not
    define, and `path` for a Windows path spelled with one that it does — the
    silent case, where `C:\\temp` is a tab and nothing anywhere says so.
    """
    issues = []
    with open(path, 'rb') as handle:
        tokens = list(tokenize.tokenize(handle.readline))
    for token in tokens:
        if token.type != tokenize.STRING:
            continue
        text = token.string
        prefix = ''
        while text and text[0] in 'rRbBuUfF':
            prefix += text[0]
            text = text[1:]
        if 'r' in prefix.lower():
            continue
        if text.startswith(TRIPLE_DOUBLE):
            quote = TRIPLE_DOUBLE
        elif text.startswith(TRIPLE_SINGLE):
            quote = TRIPLE_SINGLE
        else:
            quote = text[:1]
        if len(text) < len(quote) * 2:
            continue
        body = text[len(quote):-len(quote)]
        path_like = any(part in body for part in
                        ('C:' + BACKSLASH, 'D:' + BACKSLASH, 'E:' + BACKSLASH,
                         'tools' + BACKSLASH, 'build' + BACKSLASH, '%TEMP%' + BACKSLASH))
        index = 0
        while index < len(body):
            if body[index] != BACKSLASH:
                index += 1
                continue
            if index + 1 >= len(body):
                break
            following = body[index + 1]
            snippet = repr(body[max(0, index - 16):index + 8])
            if following not in KNOWN:
                issues.append((token.start[0], 'escape', snippet))
            elif (path_like and following in 'tnbf'
                  and index + 2 < len(body) and body[index + 2].isalnum()):
                # Only when the escape is glued to a word, as in `C:\temp`;
                # a trailing `\n` closing the line is not a misspelled path.
                issues.append((token.start[0], 'path', snippet))
            index += 2
    return issues


def main(argv):
    verbose = '-v' in argv or '--verbose' in argv
    checks = Checks(verbose)

    files = list(sources())
    checks.check('the repository has Python sources to check', len(files) > 20, str(len(files)))

    broken = []
    for path in files:
        try:
            with open(path, 'rb') as handle:
                compile(handle.read(), path, 'exec')
        except SyntaxError as problem:
            broken.append(f'{os.path.relpath(path, ROOT)}:{problem.lineno}: {problem.msg}')
    checks.check('every Python file parses', not broken, '; '.join(broken[:4]))

    escapes, wrong = [], []
    for path in files:
        for line, kind, snippet in string_issues(path):
            entry = f'{os.path.relpath(path, ROOT)}:{line} {snippet}'
            (escapes if kind == 'escape' else wrong).append(entry)
    checks.check('no string has an invalid escape sequence', not escapes,
                 '; '.join(escapes[:4]))
    checks.check('no Windows path is spelled with an escape that changes it',
                 not wrong, '; '.join(wrong[:4]))

    print(f'passed: {checks.passed}  failed: {len(checks.failed)}')
    if checks.failed:
        print('failed: ' + ', '.join(checks.failed))
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
