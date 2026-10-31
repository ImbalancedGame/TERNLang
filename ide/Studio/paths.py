"""File paths and file URIs, on any platform.

Kept away from Qt so that `tools/` — and a test — can use it without PySide6,
and so that the Windows cases can be exercised on Linux, which is where the
mistakes in this file would otherwise only show up on somebody's laptop.
"""

import os


def normalise_uri_path(value):
    """Turns a filesystem path into the path part of a file URI.

    A URI's path always begins with `/`. On POSIX an absolute path already does;
    on Windows `C:/Users/thoma` has to become `/C:/Users/thoma`, or the result
    is `file://C:/...`: a URI whose host is `C` and whose path is empty. Spaces
    are left alone, because the server takes the URI as an opaque key.

        C:\\Users\\thoma\\a.tern   ->  /C:/Users/thoma/a.tern
        /home/user/a.tern       ->  /home/user/a.tern
    """
    value = str(value).replace("\\", "/")
    if not value.startswith("/"):
        value = "/" + value
    return value


def uri_for(path):
    """The file URI for a path."""
    return "file://" + normalise_uri_path(os.path.abspath(path))


def path_for(uri):
    """The filesystem path from a file URI, on either platform.

    `/C:/Users/thoma/x.tern` is a Windows path with a URI's leading slash in
    front of it, so the slash comes off again. A POSIX path never looks like
    that — a directory literally named `C:` at the root would, and that is a
    price worth paying for Windows paths that work.

        file:///C:/Users/thoma/a.tern  ->  C:/Users/thoma/a.tern
        file:///home/user/a.tern       ->  /home/user/a.tern
    """
    if not uri.startswith("file://"):
        return uri

    value = uri[len("file://"):]
    if len(value) >= 3 and value[0] == "/" and value[2] == ":" and value[1].isalpha():
        value = value[1:]
    return value
