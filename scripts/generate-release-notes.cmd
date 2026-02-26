@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "EXIT_USAGE=2"
set "EXIT_PREREQ=3"
set "EXIT_RUNTIME=5"

set "CURRENT_TAG="
set "REPOSITORY="
set "OUTPUT_FILE=.release\release-notes.txt"

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

if not defined CURRENT_TAG (
    echo ERROR: --tag is required.
    call :print_usage
    exit /B %EXIT_USAGE%
)
if not defined REPOSITORY (
    echo ERROR: --repo is required.
    call :print_usage
    exit /B %EXIT_USAGE%
)

git rev-parse --git-dir >NUL 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: this script must run inside a git repository.
    exit /B %EXIT_PREREQ%
)

for %%I in ("%OUTPUT_FILE%") do if not exist "%%~dpI" mkdir "%%~dpI" >NUL 2>&1

set "PROJECT_NAME=%REPOSITORY%"
for /F "tokens=2 delims=/" %%P in ("%REPOSITORY%") do set "PROJECT_NAME=%%P"

set "REPO_URL=https://github.com/%REPOSITORY%"
set "FULL_PACKAGE_URL=%REPO_URL%/releases/download/%CURRENT_TAG%/fahrenheit-full-%CURRENT_TAG%.zip"
set "MOD_PACKAGE_URL=%REPO_URL%/releases/download/%CURRENT_TAG%/fhparry-mod-%CURRENT_TAG%.zip"
set "FULL_CHECKSUM_URL=%FULL_PACKAGE_URL%.sha256"
set "MOD_CHECKSUM_URL=%MOD_PACKAGE_URL%.sha256"
set "README_URL=%REPO_URL%/blob/main/README.md"
set "RELEASE_TAG_URL=%REPO_URL%/releases/tag/%CURRENT_TAG%"
set "ALL_RELEASES_URL=%REPO_URL%/releases"

for /F "delims=" %%D in ('git log -1 --date^=short --format^=%%ad "%CURRENT_TAG%^{commit}" 2^>NUL') do (
    if not defined RELEASE_DATE set "RELEASE_DATE=%%D"
)
if not defined RELEASE_DATE (
    for /F "delims=" %%D in ('git log -1 --date^=short --format^=%%ad') do (
        if not defined RELEASE_DATE set "RELEASE_DATE=%%D"
    )
)

set "PREVIOUS_TAG="
for /F "delims=" %%T in ('git tag --sort=-v:refname') do (
    if /I not "%%T"=="%CURRENT_TAG%" if not defined PREVIOUS_TAG set "PREVIOUS_TAG=%%T"
)

set "COMMIT_TMP=%TEMP%\faparry-commits-%RANDOM%%RANDOM%.tmp"
if defined PREVIOUS_TAG (
    set "RANGE=%PREVIOUS_TAG%..%CURRENT_TAG%"
    set "CHANGELOG_LINK=Full Changelog: %REPO_URL%/compare/%PREVIOUS_TAG%...%CURRENT_TAG%"
    > "%COMMIT_TMP%" (
        for /F "delims=" %%L in ('git log --pretty^=format:- %%s ^([%%h]^(%REPO_URL%/commit/%%H^)^) --no-merges !RANGE! 2^>NUL') do echo %%L
    )
) else (
    set "CHANGELOG_LINK=Full Changelog: %REPO_URL%/commits/%CURRENT_TAG%"
    > "%COMMIT_TMP%" echo - Initial release
)

for %%I in ("%COMMIT_TMP%") do (
    if %%~zI EQU 0 (
        > "%COMMIT_TMP%" echo - Initial release
    )
)

> "%OUTPUT_FILE%" echo # %PROJECT_NAME% %CURRENT_TAG% (%RELEASE_DATE%)
>> "%OUTPUT_FILE%" echo.
>> "%OUTPUT_FILE%" echo This release provides pre-built ZIP packages for Windows ^(FFX/X-2 HD Remaster + Fahrenheit^):
>> "%OUTPUT_FILE%" echo.
>> "%OUTPUT_FILE%" echo - Full package: [fahrenheit-full-%CURRENT_TAG%.zip](%FULL_PACKAGE_URL%)
>> "%OUTPUT_FILE%" echo   - SHA256: [fahrenheit-full-%CURRENT_TAG%.zip.sha256](%FULL_CHECKSUM_URL%)
>> "%OUTPUT_FILE%" echo - Mod-only package: [fhparry-mod-%CURRENT_TAG%.zip](%MOD_PACKAGE_URL%)
>> "%OUTPUT_FILE%" echo   - SHA256: [fhparry-mod-%CURRENT_TAG%.zip.sha256](%MOD_CHECKSUM_URL%)
>> "%OUTPUT_FILE%" echo.
>> "%OUTPUT_FILE%" echo ## Installation
>> "%OUTPUT_FILE%" echo.
>> "%OUTPUT_FILE%" echo 1. Download one of the ZIP packages above.
>> "%OUTPUT_FILE%" echo 2. Extract into your game directory ^(folder containing `FFX.exe`^).
>> "%OUTPUT_FILE%" echo 3. Launch through your normal Fahrenheit flow.
>> "%OUTPUT_FILE%" echo.
>> "%OUTPUT_FILE%" echo ## Changes in This Release
>> "%OUTPUT_FILE%" echo.
>> "%OUTPUT_FILE%" echo [View this tag](%RELEASE_TAG_URL%) ^| [All Releases](%ALL_RELEASES_URL%)
>> "%OUTPUT_FILE%" echo.
type "%COMMIT_TMP%" >> "%OUTPUT_FILE%"
>> "%OUTPUT_FILE%" echo.
>> "%OUTPUT_FILE%" echo ---
>> "%OUTPUT_FILE%" echo.
>> "%OUTPUT_FILE%" echo %CHANGELOG_LINK% ^| [README](%README_URL%)

if exist "%COMMIT_TMP%" del /F /Q "%COMMIT_TMP%" >NUL 2>&1

echo Release notes generated: %OUTPUT_FILE%
exit /B 0

:print_usage
echo Usage: scripts\generate-release-notes.cmd --tag vX.Y.Z --repo owner/repo [--out .release\release-notes.txt]
echo.
echo Example:
echo   scripts\generate-release-notes.cmd --tag v1.2.3 --repo Exoridus/fahrenheit-parry-mod --out .release\release-notes.txt
exit /B 0
