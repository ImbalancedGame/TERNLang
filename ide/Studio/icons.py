"""The few icons the window chrome needs, drawn as SVG and cached on disk.

Qt stylesheets take a URL, not a QIcon, so a stylesheet that wants a close
cross, a check mark or a chevron has to point at a file. Shipping PNGs would
mean binaries in the repository and a second set for every palette, and
letting the style draw them gives whatever the platform thinks a close button
looks like — which on Fusion is a red square and on Windows is a small grey
bevel, neither of which belongs in a window that is trying to look like Rider.

So the shapes are SVG, written once per colour into the temporary directory and
referenced by path. Vector means crisp at any scale factor, one file per colour
means five palettes cost five little files, and nothing binary is committed.
"""

import os
import tempfile

# Every shape is a 16x16 viewBox drawn with strokes, so one definition works at
# 10px in a spin box and 24px on a display with 200% scaling.
SHAPES = {
    "close": '<path d="M4.5 4.5 L11.5 11.5 M11.5 4.5 L4.5 11.5" stroke="{colour}" '
             'stroke-width="1.6" stroke-linecap="round"/>',
    "check": '<path d="M3.5 8.5 L6.5 11.5 L12.5 4.5" stroke="{colour}" '
             'stroke-width="2" stroke-linecap="round" stroke-linejoin="round" fill="none"/>',
    "chevron-down": '<path d="M4 6.5 L8 10.5 L12 6.5" stroke="{colour}" '
                    'stroke-width="1.7" stroke-linecap="round" '
                    'stroke-linejoin="round" fill="none"/>',
    "chevron-up": '<path d="M4 9.5 L8 5.5 L12 9.5" stroke="{colour}" '
                  'stroke-width="1.7" stroke-linecap="round" '
                  'stroke-linejoin="round" fill="none"/>',
    "chevron-right": '<path d="M6.5 4 L10.5 8 L6.5 12" stroke="{colour}" '
                     'stroke-width="1.7" stroke-linecap="round" '
                     'stroke-linejoin="round" fill="none"/>',
    "dot": '<circle cx="8" cy="8" r="2.6" fill="{colour}"/>',
    "square": '<rect x="4.5" y="4.5" width="7" height="7" rx="1.5" fill="{colour}"/>',
    "play": '<path d="M5.5 3.8 L12.4 8 L5.5 12.2 Z" fill="{colour}" stroke="{colour}" '
            'stroke-width="1" stroke-linejoin="round"/>',
    "stop": '<rect x="4.4" y="4.4" width="7.2" height="7.2" rx="1.6" fill="{colour}"/>',
    "gear": '<circle cx="8" cy="8" r="3.1" stroke="{colour}" stroke-width="1.5" fill="none"/>'
            '<path d="M8 1.9 V3.3 M8 12.7 V14.1 M1.9 8 H3.3 M12.7 8 H14.1 '
            'M3.7 3.7 L4.7 4.7 M11.3 11.3 L12.3 12.3 M12.3 3.7 L11.3 4.7 '
            'M4.7 11.3 L3.7 12.3" stroke="{colour}" stroke-width="1.5" stroke-linecap="round"/>',
    "search": '<circle cx="7" cy="7" r="3.9" stroke="{colour}" stroke-width="1.6" fill="none"/>'
              '<path d="M10.1 10.1 L13.4 13.4" stroke="{colour}" stroke-width="1.7" '
              'stroke-linecap="round"/>',
    "lines": '<path d="M3 5 H13 M3 8 H9.5 M3 11 H11.5" stroke="{colour}" stroke-width="1.6" '
             'stroke-linecap="round"/>',
    "warning": '<path d="M8 3 L13.4 12.6 H2.6 Z" stroke="{colour}" stroke-width="1.5" '
               'stroke-linejoin="round" fill="none"/>'
               '<path d="M8 6.6 V9.2" stroke="{colour}" stroke-width="1.5" stroke-linecap="round"/>'
               '<circle cx="8" cy="11" r="0.85" fill="{colour}"/>',
    "bug": '<ellipse cx="8" cy="8.8" rx="2.9" ry="3.5" stroke="{colour}" stroke-width="1.5" '
           'fill="none"/>'
           '<path d="M5.4 5.9 L3.9 4.2 M10.6 5.9 L12.1 4.2 M4.2 8.8 H2.2 M13.8 8.8 H11.8 '
           'M5 11.3 L3.6 12.9 M11 11.3 L12.4 12.9" stroke="{colour}" stroke-width="1.4" '
           'stroke-linecap="round"/>',
}

_cache = {}


def directory():
    """Where the generated icons live: one directory, created on first use."""
    path = os.path.join(tempfile.gettempdir(), "tern-studio-icons")
    os.makedirs(path, exist_ok=True)
    return path


def path(name, colour, size=16):
    """The file holding `name` in `colour`, writing it if it is not there yet.

    Returns a path suitable for `url("...")` in a stylesheet, with forward
    slashes whatever the platform uses, because Qt parses the stylesheet URL
    itself and a backslash is an escape character there.
    """
    key = (name, colour, size)
    if key in _cache:
        return _cache[key]

    shape = SHAPES.get(name, SHAPES["dot"]).format(colour=colour if colour.startswith("#") else "#" + colour)
    body = (f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" '
            f'viewBox="0 0 16 16" fill="none">{shape}</svg>')
    filename = f"{name}-{colour.lstrip('#')}-{size}.svg"
    full = os.path.join(directory(), filename)
    if not os.path.exists(full):
        with open(full, "w", encoding="utf-8") as handle:
            handle.write(body)
    _cache[key] = full.replace("\\", "/")
    return _cache[key]


def qicon(name, colour, size=16):
    """The same shape as a QIcon, for toolbars and menus."""
    from PySide6.QtGui import QIcon

    return QIcon(path(name, colour, size))


def url(name, colour, size=16):
    """The same path, ready to paste into a stylesheet as `url("...")`."""
    return f'url("{path(name, colour, size)}")'
