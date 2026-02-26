@echo off
setlocal EnableExtensions

set "EXIT_USAGE=2"
set "EXIT_RUNTIME=5"

set "RANGE="
set "SCRIPT_DIR=%~dp0"

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--range" (
    if "%~2"=="" (
        echo ERROR: --range requires a value.
        exit /B %EXIT_USAGE%
    )
    set "RANGE=%~2"
    shift
    shift
    goto parse_args
)
if /I "%~1"=="--help" (
    call :print_usage
    exit /B 0
)
echo ERROR: unknown argument "%~1".
call :print_usage
exit /B %EXIT_USAGE%

:args_done

if not defined RANGE (
    echo ERROR: --range is required.
    call :print_usage
    exit /B %EXIT_USAGE%
)

git rev-parse --git-dir >NUL 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: this script must run inside a git repository.
    exit /B %EXIT_RUNTIME%
)

for /F "usebackq delims=" %%S in (`git log --format^=%%s --no-merges "%RANGE%"`) do (
    call "%SCRIPT_DIR%validate-commit-msg.cmd" --message "%%S"
    if errorlevel 1 (
        echo ERROR: invalid commit message found in range %RANGE%.
        echo   %%S
        exit /B %EXIT_RUNTIME%
    )
)

echo Commit messages valid for range %RANGE%.
exit /B 0

:print_usage
echo Usage: scripts\validate-commit-range.cmd --range BASE..HEAD
echo.
echo Example:
echo   scripts\validate-commit-range.cmd --range origin/main..HEAD
exit /B 0
