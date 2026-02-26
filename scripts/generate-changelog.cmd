@echo off
setlocal EnableExtensions

set "EXIT_USAGE=2"
set "EXIT_PREREQ=3"
set "EXIT_RUNTIME=5"

set "CURRENT_TAG="
set "REPOSITORY="
set "OUTPUT_FILE=CHANGELOG.md"

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--tag" (
    if "%~2"=="" (
        echo ERROR: --tag requires a value.
        exit /B %EXIT_USAGE%
    )
    set "CURRENT_TAG=%~2"
    shift
    shift
    goto parse_args
)
if /I "%~1"=="--repo" (
    if "%~2"=="" (
        echo ERROR: --repo requires a value.
        exit /B %EXIT_USAGE%
    )
    set "REPOSITORY=%~2"
    shift
    shift
    goto parse_args
)
if /I "%~1"=="--out" (
    if "%~2"=="" (
        echo ERROR: --out requires a value.
        exit /B %EXIT_USAGE%
    )
    set "OUTPUT_FILE=%~2"
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

git rev-parse --git-dir >NUL 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: this script must run inside a git repository.
    exit /B %EXIT_PREREQ%
)

if not defined REPOSITORY call :resolve_repo
if defined REPOSITORY set "REPO_URL=https://github.com/%REPOSITORY%"

call :resolve_current_label
if %ERRORLEVEL% NEQ 0 exit /B %EXIT_RUNTIME%

set "PREVIOUS_TAG="
if /I not "%CURRENT_LABEL%"=="Initial Commit" call :resolve_previous_tag "%CURRENT_LABEL%" PREVIOUS_TAG

for /F "delims=" %%D in ('git log -1 --date^=short --format^=%%ad "%CURRENT_REF%" 2^>NUL') do (
    if not defined RELEASE_DATE set "RELEASE_DATE=%%D"
)
if not defined RELEASE_DATE (
    for /F "delims=" %%D in ('git log -1 --date^=short --format^=%%ad') do (
        if not defined RELEASE_DATE set "RELEASE_DATE=%%D"
    )
)

if /I "%CURRENT_LABEL%"=="Initial Commit" (
    set "RANGE=HEAD"
) else if defined PREVIOUS_TAG (
    set "RANGE=%PREVIOUS_TAG%..%CURRENT_REF%"
) else (
    set "RANGE=%CURRENT_REF%"
)

for /F "delims=" %%S in ('git rev-parse --short "%CURRENT_REF%" 2^>NUL') do if not defined CURRENT_SHORT set "CURRENT_SHORT=%%S"
for /F "delims=" %%S in ('git rev-parse "%CURRENT_REF%" 2^>NUL') do if not defined CURRENT_FULL set "CURRENT_FULL=%%S"

set "COMMIT_TMP=%TEMP%\faparry-changelog-%RANDOM%%RANDOM%.tmp"
if defined REPO_URL (
    > "%COMMIT_TMP%" (
        for /F "usebackq delims=" %%L in (`git log --pretty^=format:- %%s ^([%%h]^(%REPO_URL%/commit/%%H^)^) --no-merges "%RANGE%" 2^>NUL`) do @echo %%L
    )
) else (
    > "%COMMIT_TMP%" (
        for /F "usebackq delims=" %%L in (`git log --pretty^=format:- %%s ^(%%h^) --no-merges "%RANGE%" 2^>NUL`) do @echo %%L
    )
)

for %%I in ("%OUTPUT_FILE%") do if not exist "%%~dpI" mkdir "%%~dpI" >NUL 2>&1

> "%OUTPUT_FILE%" echo # Changelog
>> "%OUTPUT_FILE%" echo.

if /I "%CURRENT_LABEL%"=="Initial Commit" (
    if defined REPO_URL (
        >> "%OUTPUT_FILE%" echo ## [Initial Commit]^(%REPO_URL%/tree/%CURRENT_FULL%^) ^(%RELEASE_DATE%^)
        >> "%OUTPUT_FILE%" echo [Commit History]^(%REPO_URL%/commits/%CURRENT_FULL%^)
    ) else (
        >> "%OUTPUT_FILE%" echo ## Initial Commit ^(%RELEASE_DATE%^)
    )
) else (
    if defined REPO_URL (
        >> "%OUTPUT_FILE%" echo ## [%CURRENT_LABEL%]^(%REPO_URL%/releases/tag/%CURRENT_LABEL%^) ^(%RELEASE_DATE%^)
        if defined PREVIOUS_TAG (
            >> "%OUTPUT_FILE%" echo [Full Changelog]^(%REPO_URL%/compare/%PREVIOUS_TAG%...%CURRENT_LABEL%^) ^| [Previous Releases]^(%REPO_URL%/releases^)
        ) else (
            >> "%OUTPUT_FILE%" echo [Initial Release Commits]^(%REPO_URL%/commits/%CURRENT_REF%^) ^| [All Releases]^(%REPO_URL%/releases^)
        )
    ) else (
        >> "%OUTPUT_FILE%" echo ## %CURRENT_LABEL% ^(%RELEASE_DATE%^)
    )
)

>> "%OUTPUT_FILE%" echo.
for %%I in ("%COMMIT_TMP%") do (
    if %%~zI EQU 0 (
        >> "%OUTPUT_FILE%" echo - Initial commit.
    ) else (
        type "%COMMIT_TMP%" >> "%OUTPUT_FILE%"
    )
)

if exist "%COMMIT_TMP%" del /F /Q "%COMMIT_TMP%" >NUL 2>&1

echo Changelog generated: %OUTPUT_FILE%
exit /B 0

:resolve_current_label
if defined CURRENT_TAG (
    set "CURRENT_LABEL=%CURRENT_TAG%"
    git rev-parse "%CURRENT_TAG%^{commit}" >NUL 2>&1
    if %ERRORLEVEL% EQU 0 (
        set "CURRENT_REF=%CURRENT_TAG%"
    ) else (
        set "CURRENT_REF=HEAD"
    )
    exit /B 0
)

set "LATEST_SEMVER_TAG="
for /F "delims=" %%T in ('git tag --list "v*" --sort=-v:refname') do (
    echo %%T | findstr /R "^v[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >NUL && if not defined LATEST_SEMVER_TAG set "LATEST_SEMVER_TAG=%%T"
)

if defined LATEST_SEMVER_TAG (
    set "CURRENT_LABEL=%LATEST_SEMVER_TAG%"
    set "CURRENT_REF=%LATEST_SEMVER_TAG%"
) else (
    set "CURRENT_LABEL=Initial Commit"
    set "CURRENT_REF=HEAD"
)
exit /B 0

:resolve_previous_tag
setlocal
set "CURRENT=%~1"
set "PREV="
for /F "delims=" %%T in ('git tag --list "v*" --sort=-v:refname') do (
    echo %%T | findstr /R "^v[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >NUL && if /I not "%%T"=="%CURRENT%" if not defined PREV set "PREV=%%T"
)
endlocal & set "%~2=%PREV%"
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
endlocal & set "REPOSITORY=%LOCAL_REPO%"
exit /B 0

:print_usage
echo Usage: scripts\generate-changelog.cmd [--tag vX.Y.Z] [--repo owner/repo] [--out CHANGELOG.md]
echo.
echo Examples:
echo   scripts\generate-changelog.cmd --out CHANGELOG.md
echo   scripts\generate-changelog.cmd --tag v1.2.3 --repo Exoridus/fahrenheit-parry-mod --out CHANGELOG.md
exit /B 0
