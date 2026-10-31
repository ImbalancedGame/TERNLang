"""TERN's IDE: a syntax server, a debugger and the Studio window.

A regular package rather than a namespace one, so `import ide.Studio` cannot be
shadowed by a script called `ide.py` earlier on sys.path — which is exactly what
tools/ide.py used to do to itself.
"""
