#!/usr/bin/env bash
# Regenerates docs/stdlib.md from the standard library registered in the engine,
# so the reference can never drift from the implementation.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

"$HERE/tools/build.py" >/dev/null 2>&1 || true

# The generated page starts with its own front matter; keep the prose intro
# from the existing file so the shell of the document stays hand written.
"$HERE/tools/tern" docs > "$HERE/docs/stdlib.md"
echo "wrote docs/stdlib.md ($(wc -l < "$HERE/docs/stdlib.md") lines)"

# The MSDN-style reference: one page per function and per method, generated from
# the same catalogue plus the prose in tools/reference/.
python3 "$HERE/tools/gen-reference.py"
