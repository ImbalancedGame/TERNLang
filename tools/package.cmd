@echo off
rem Builds the double-clickable TERN Studio. Same flags as package.py:
rem   tools\package.cmd              dist\TERN Studio\TERN Studio.exe
rem   tools\package.cmd --onefile    one executable
rem   tools\package.cmd --clean      start from scratch
rem
rem Uses the virtualenv when there is one, because PyInstaller has to run in
rem the same Python that imports PySide6 -- installing it into a different
rem interpreter is the classic way to get a bundle with no Qt in it.
setlocal
set "HERE=%~dp0.."
if exist "%HERE%\.venv\Scripts\python.exe" (
    "%HERE%\.venv\Scripts\python.exe" "%~dp0package.py" %*
) else (
    python "%~dp0package.py" %*
)
exit /b %ERRORLEVEL%
