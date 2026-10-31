@echo off
rem Builds a TERN script into a standalone native binary.
rem
rem   native\build.cmd app.tern -o app
rem   native\build.cmd app.tern -o app --cflags=-O3
rem
rem The bash twin is native/build.sh; both run native\ternc.py.
python "%~dp0ternc.py" %*
exit /b %ERRORLEVEL%
