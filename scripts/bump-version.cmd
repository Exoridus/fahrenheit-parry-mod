@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "EXIT_USAGE=2"
set "EXIT_PREREQ=3"
set "EXIT_STATE=4"
set "EXIT_RUNTIME=5"

set "BUMP=patch"
set "REPOSITORY="
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fI"
set "MANIFEST_FILE=%REPO_ROOT%\fhparry.manifest.json"

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--bump" (
    if "%~2"=="" (
        echo ERROR: --bump requires a value.
        exit /B %EXIT_USAGE%
    )
    set "BUMP=%~2"
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
if /I "%~1"=="--help" (
    call :print_usage
    exit /B 0
)
echo ERROR: unknown argument "%~1".
call :print_usage
exit /B %EXIT_USAGE%

:args_done

if /I not "%BUMP%"=="major" if /I not "%BUMP%"=="minor" if /I not "%BUMP%"=="patch" (
    echo ERROR: invalid bump "%BUMP%". Use one of: major, minor, patch.
    exit /B %EXIT_USAGE%
)

git rev-parse --git-dir >NUL 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: this script must run inside a git repository.
    exit /B %EXIT_PREREQ%
)

git update-index -q --refresh >NUL 2>&1
git diff --quiet --exit-code >NUL 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: working tree has unstaged changes.
    exit /B %EXIT_STATE%
)
git diff --cached --quiet --exit-code >NUL 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: working tree has staged but uncommitted changes.
    exit /B %EXIT_STATE%
)
for /F "delims=" %%U in ('git ls-files --others --exclude-standard') do (
    echo ERROR: working tree has untracked files.
    exit /B %EXIT_STATE%
)

set "LATEST_TAG="
for /F "delims=" %%T in ('git tag --list "v*" --sort=-v:refname') do (
    echo %%T | findstr /R "^v[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >NUL
    if !ERRORLEVEL! EQU 0 if not defined LATEST_TAG set "LATEST_TAG=%%T"
)
if not defined LATEST_TAG set "LATEST_TAG=v0.0.0"

call :parse_semver "%LATEST_TAG%" MAJOR MINOR PATCH
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: failed to parse latest tag "%LATEST_TAG%".
    exit /B %EXIT_RUNTIME%
)

if /I "%BUMP%"=="major" (
    set /A MAJOR+=1
    set "MINOR=0"
    set "PATCH=0"
) else if /I "%BUMP%"=="minor" (
    set /A MINOR+=1
    set "PATCH=0"
) else (
    set /A PATCH+=1
)

set "NEW_TAG=v%MAJOR%.%MINOR%.%PATCH%"
set "NEW_VERSION=%MAJOR%.%MINOR%.%PATCH%"
git rev-parse "%NEW_TAG%" >NUL 2>&1
if %ERRORLEVEL% EQU 0 (
    echo ERROR: tag "%NEW_TAG%" already exists.
    exit /B %EXIT_RUNTIME%
)

echo Latest tag: %LATEST_TAG%
echo Bump:      %BUMP%
echo New tag:    %NEW_TAG%

if defined REPOSITORY (
    call "%SCRIPT_DIR%generate-changelog.cmd" --repo "%REPOSITORY%" --out "CHANGELOG.md" --tag "%NEW_TAG%"
) else (
    call "%SCRIPT_DIR%generate-changelog.cmd" --out "CHANGELOG.md" --tag "%NEW_TAG%"
)
if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%

if not defined REPOSITORY call :resolve_repo
set "REPO_LINK="
if defined REPOSITORY set "REPO_LINK=https://github.com/%REPOSITORY%"
call :update_manifest "%MANIFEST_FILE%" "%NEW_VERSION%" "%REPO_LINK%"
if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%

git add CHANGELOG.md "%MANIFEST_FILE%"
set "COMMIT_MSG=chore(release): %NEW_TAG%"
git commit -m "%COMMIT_MSG%" >NUL 2>&1
if %ERRORLEVEL% NEQ 0 (
    git commit --allow-empty -m "%COMMIT_MSG%" >NUL 2>&1
    if %ERRORLEVEL% NEQ 0 (
        echo ERROR: failed to create release commit.
        exit /B %EXIT_RUNTIME%
    )
)

git tag -a "%NEW_TAG%" -m "%COMMIT_MSG%"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: failed to create git tag "%NEW_TAG%".
    exit /B %EXIT_RUNTIME%
)

echo.
echo Created release commit and tag:
echo   %COMMIT_MSG%
echo.
echo Next step:
echo   git push origin main --follow-tags
exit /B 0

:print_usage
echo Usage: scripts\bump-version.cmd [--bump patch^|minor^|major] [--repo owner/repo]
echo.
echo Examples:
echo   scripts\bump-version.cmd --bump patch
echo   scripts\bump-version.cmd --bump minor --repo Exoridus/fahrenheit-parry-mod
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

:update_manifest
setlocal EnableDelayedExpansion
set "MANIFEST=%~1"
set "VERSION=%~2"
set "LINK=%~3"
set "TMP_FILE=%TEMP%\faparry-manifest-%RANDOM%%RANDOM%.tmp"

if not exist "!MANIFEST!" (
    echo ERROR: manifest file not found: "!MANIFEST!"
    endlocal & exit /B %EXIT_RUNTIME%
)

> "!TMP_FILE!" (
    for /F "usebackq delims=" %%L in (`findstr /n "^" "!MANIFEST!"`) do (
        set "RAW_LINE=%%L"
        set "LINE_TEXT="
        for /F "tokens=1* delims=:" %%A in ("!RAW_LINE!") do set "LINE_TEXT=%%B"

        echo(!LINE_TEXT! | findstr /C:"\"Version\":" >NUL
        if !ERRORLEVEL! EQU 0 (
            set "LINE_TEXT=    \"Version\": \"!VERSION!\","
        )

        if defined LINK (
            echo(!LINE_TEXT! | findstr /C:"\"Link\":" >NUL
            if !ERRORLEVEL! EQU 0 (
                set "LINE_TEXT=    \"Link\": \"!LINK!\","
            )
        )

        echo(!LINE_TEXT!
    )
)

if !ERRORLEVEL! NEQ 0 (
    if exist "!TMP_FILE!" del /F /Q "!TMP_FILE!" >NUL 2>&1
    echo ERROR: failed to rewrite manifest file.
    endlocal & exit /B %EXIT_RUNTIME%
)

move /Y "!TMP_FILE!" "!MANIFEST!" >NUL
if !ERRORLEVEL! NEQ 0 (
    if exist "!TMP_FILE!" del /F /Q "!TMP_FILE!" >NUL 2>&1
    echo ERROR: failed to update manifest file.
    endlocal & exit /B %EXIT_RUNTIME%
)

echo Updated manifest metadata: !MANIFEST!
endlocal & exit /B 0

:parse_semver
setlocal EnableDelayedExpansion
set "TAG=%~1"
if /I not "!TAG:~0,1!"=="v" exit /B 1
set "NUM=!TAG:~1!"
for /F "tokens=1-3 delims=." %%A in ("!NUM!") do (
    set "A=%%A"
    set "B=%%B"
    set "C=%%C"
)
if not defined A exit /B 1
if not defined B exit /B 1
if not defined C exit /B 1
echo !A!| findstr /R "^[0-9][0-9]*$" >NUL || exit /B 1
echo !B!| findstr /R "^[0-9][0-9]*$" >NUL || exit /B 1
echo !C!| findstr /R "^[0-9][0-9]*$" >NUL || exit /B 1
endlocal & (
    set "%~2=%A%"
    set "%~3=%B%"
    set "%~4=%C%"
)
exit /B 0
