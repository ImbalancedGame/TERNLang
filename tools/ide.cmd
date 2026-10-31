@echo off
rem Launches TERN Studio on Windows. The logic lives in tools\studio.py, which is
rem also what tools\ide.sh runs, so the two cannot drift.
setlocal
python "%~dp0studio.py" %*
exit /b %ERRORLEVEL%
