@echo off
setlocal EnableExtensions

set "EXIT_PREREQ=3"
set "EXIT_RUNTIME=5"
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fI"
set "HOOK_FILE=%REPO_ROOT%\.githooks\commit-msg"

git rev-parse --git-dir >NUL 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: this script must run inside a git repository.
    exit /B %EXIT_PREREQ%
)

if not exist "%HOOK_FILE%" (
    echo ERROR: missing hook file: "%HOOK_FILE%"
    exit /B %EXIT_RUNTIME%
)

git config --local core.hooksPath .githooks
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: failed to configure core.hooksPath.
    exit /B %EXIT_RUNTIME%
)

echo Installed git hooks path: .githooks
exit /B 0
