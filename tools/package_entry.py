"""PyInstaller entry point. A script, not a module: the bundler wants a file.

Everything it does is in `ide.Studio.main`, which is the same code the
`python -m ide.Studio` launcher runs — so a packaged Studio and a source
checkout cannot drift apart.
"""

import os
import sys

# Frozen, `__file__` lives inside the bundle and the repository is not on the
# path any more; the modules were collected into it.
if not getattr(sys, "frozen", False):
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from ide.Studio.main import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())
