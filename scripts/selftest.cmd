@echo off
setlocal EnableExtensions

set "EXIT_PREREQ=3"
set "EXIT_RUNTIME=5"

set "REPO="
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fI"

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--repo" (
    if "%~2"=="" (
        echo ERROR: --repo requires a value.
        exit /B 2
    )
    set "REPO=%~2"
    shift
    shift
    goto parse_args
)
if /I "%~1"=="--help" (
    echo Usage: scripts\selftest.cmd [--repo owner/repo]
    exit /B 0
)
echo ERROR: unknown argument "%~1".
echo Usage: scripts\selftest.cmd [--repo owner/repo]
exit /B 2

:args_done

git rev-parse --git-dir >NUL 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: this script must run inside a git repository.
    exit /B %EXIT_PREREQ%
)

if not defined REPO call :resolve_repo
if not defined REPO (
    echo ERROR: could not resolve repo slug. Pass --repo owner/repo.
    exit /B %EXIT_RUNTIME%
)

if not exist "%REPO_ROOT%\.release" mkdir "%REPO_ROOT%\.release" >NUL 2>&1

call "%SCRIPT_DIR%generate-changelog.cmd" --repo "%REPO%" --out ".release\selftest-changelog.md"
if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%

call "%SCRIPT_DIR%generate-release-notes.cmd" --tag "v0.0.1" --repo "%REPO%" --out ".release\selftest-release-notes.txt"
if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%

> "%REPO_ROOT%\.release\selftest-valid-commit.txt" echo feat: selftest commit format
call "%SCRIPT_DIR%validate-commit-msg.cmd" "%REPO_ROOT%\.release\selftest-valid-commit.txt"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: commit message validator rejected a valid message.
    exit /B %EXIT_RUNTIME%
)
> "%REPO_ROOT%\.release\selftest-invalid-commit.txt" echo invalid commit message
call "%SCRIPT_DIR%validate-commit-msg.cmd" "%REPO_ROOT%\.release\selftest-invalid-commit.txt" >NUL 2>&1
if %ERRORLEVEL% EQU 0 (
    echo ERROR: commit message validator accepted an invalid message.
    exit /B %EXIT_RUNTIME%
)

if not exist "%REPO_ROOT%\.release\selftest-changelog.md" (
    echo ERROR: missing selftest changelog output.
    exit /B %EXIT_RUNTIME%
)
if not exist "%REPO_ROOT%\.release\selftest-release-notes.txt" (
    echo ERROR: missing selftest release-notes output.
    exit /B %EXIT_RUNTIME%
)

make -n build BUILD_TARGET=mod CONFIGURATION=Debug >NUL
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: make selftest failed for build target.
    exit /B %EXIT_RUNTIME%
)
make -n deploy DEPLOY_TARGET=mod DEPLOY_MODE=merge GAME_DIR="C:\Games\Final Fantasy X-X2 - HD Remaster" >NUL
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: make selftest failed for deploy target.
    exit /B %EXIT_RUNTIME%
)

if exist "%REPO_ROOT%\.release\selftest-valid-commit.txt" del /F /Q "%REPO_ROOT%\.release\selftest-valid-commit.txt" >NUL 2>&1
if exist "%REPO_ROOT%\.release\selftest-invalid-commit.txt" del /F /Q "%REPO_ROOT%\.release\selftest-invalid-commit.txt" >NUL 2>&1

echo Selftest passed.
exit /B 0

:resolve_repo
setlocal EnableDelayedExpansion
set "LOCAL_REMOTE_URL="
set "LOCAL_REPO="
for /F "delims=" %%U in ('git remote get-url origin 2^>NUL') do set "LOCAL_REMOTE_URL=%%U"
if defined LOCAL_REMOTE_URL (
    set "CANDIDATE=!LOCAL_REMOTE_URL:git@github.com:=!"
    if /I not "!CANDIDATE!"=="!LOCAL_REMOTE_URL!" set "LOCAL_REPO=!CANDIDATE!"
)
if not defined LOCAL_REPO if defined LOCAL_REMOTE_URL (
    set "CANDIDATE=!LOCAL_REMOTE_URL:https://github.com/=!"
    if /I not "!CANDIDATE!"=="!LOCAL_REMOTE_URL!" set "LOCAL_REPO=!CANDIDATE!"
)
if defined LOCAL_REPO set "LOCAL_REPO=!LOCAL_REPO:.git=!"
endlocal & set "REPO=%LOCAL_REPO%"
exit /B 0
