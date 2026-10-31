@echo off
rem Runs the compiled TERN CLI. The bash twin is tools/tern; both start the same
rem assembly, so `tern run app.tern` means the same thing on either platform.
rem
rem A bundled toolchain (the sandbox bootstrap's build\dotnet.exe) is used when
rem present, otherwise whatever `dotnet` is on PATH.
setlocal
set "HERE=%~dp0.."
if not defined TERN_TOOLCHAIN_DIR set "TERN_TOOLCHAIN_DIR=%TEMP%\tern-toolchain"

if exist "%HERE%\build\dotnet.exe" (
    set "DOTNET_ROOT=%TERN_TOOLCHAIN_DIR%\dotnet"
    "%HERE%\build\dotnet.exe" "%HERE%\build\TERN.Runner.dll" %*
) else (
    dotnet "%HERE%\build\TERN.Runner.dll" %*
)
exit /b %ERRORLEVEL%
