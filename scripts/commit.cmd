@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "EXIT_USAGE=2"
set "EXIT_RUNTIME=5"

set "TYPE=chore"
set "SCOPE="
set "MESSAGE="
set "BREAKING=false"
set "SCRIPT_DIR=%~dp0"

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--type" (
    if "%~2"=="" (
        echo ERROR: --type requires a value.
        exit /B %EXIT_USAGE%
    )
    set "TYPE=%~2"
    shift
    shift
    goto parse_args
)
if /I "%~1"=="--scope" (
    if "%~2"=="" (
        echo ERROR: --scope requires a value.
        exit /B %EXIT_USAGE%
    )
    set "SCOPE=%~2"
    shift
    shift
    goto parse_args
)
if /I "%~1"=="--message" (
    if "%~2"=="" (
        echo ERROR: --message requires a value.
        exit /B %EXIT_USAGE%
    )
    set "MESSAGE=%~2"
    shift
    shift
    goto parse_args
)
if /I "%~1"=="--breaking" (
    set "BREAKING=true"
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

if not defined MESSAGE (
    echo ERROR: --message is required.
    call :print_usage
    exit /B %EXIT_USAGE%
)

set "SUBJECT=%TYPE%"
if defined SCOPE set "SUBJECT=%SUBJECT%(%SCOPE%)"
if /I "%BREAKING%"=="true" set "SUBJECT=%SUBJECT%!"
set "SUBJECT=%SUBJECT%: %MESSAGE%"

call "%SCRIPT_DIR%validate-commit-msg.cmd" --message "%SUBJECT%"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: refusing to commit invalid Conventional Commit subject.
    exit /B %EXIT_USAGE%
)

git commit -m "%SUBJECT%"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: git commit failed.
    exit /B %EXIT_RUNTIME%
)

echo Commit created:
echo   %SUBJECT%
exit /B 0

:print_usage
echo Usage: scripts\commit.cmd --message "summary" [--type feat^|fix^|docs^|chore^|... ] [--scope value] [--breaking]
echo.
echo Examples:
echo   scripts\commit.cmd --message "update readme"
echo   scripts\commit.cmd --type feat --scope ui --message "add timing mode toggle"
exit /B 0
