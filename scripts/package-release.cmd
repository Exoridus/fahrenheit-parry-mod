@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "EXIT_USAGE=2"
set "EXIT_PREREQ=3"
set "EXIT_RUNTIME=5"

set "TAG="
set "DEPLOY_DIR=.workspace\fahrenheit\artifacts\deploy\rel"
set "MOD_ID=fhparry"
set "OUT_DIR=.release"
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fI"

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--tag" (
    if "%~2"=="" (
        echo ERROR: --tag requires a value.
        exit /B %EXIT_USAGE%
    )
    set "TAG=%~2"
    shift
    shift
    goto parse_args
)
if /I "%~1"=="--deploy-dir" (
    if "%~2"=="" (
        echo ERROR: --deploy-dir requires a value.
        exit /B %EXIT_USAGE%
    )
    set "DEPLOY_DIR=%~2"
    shift
    shift
    goto parse_args
)
if /I "%~1"=="--mod-id" (
    if "%~2"=="" (
        echo ERROR: --mod-id requires a value.
        exit /B %EXIT_USAGE%
    )
    set "MOD_ID=%~2"
    shift
    shift
    goto parse_args
)
if /I "%~1"=="--out-dir" (
    if "%~2"=="" (
        echo ERROR: --out-dir requires a value.
        exit /B %EXIT_USAGE%
    )
    set "OUT_DIR=%~2"
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

if not defined TAG (
    echo ERROR: --tag is required.
    call :print_usage
    exit /B %EXIT_USAGE%
)

call :resolve_path "%DEPLOY_DIR%" DEPLOY_DIR_ABS
call :resolve_path "%OUT_DIR%" OUT_DIR_ABS

set "MOD_DEPLOY_DIR=%DEPLOY_DIR_ABS%\mods\%MOD_ID%"
set "FULL_ROOT=%OUT_DIR_ABS%\full"
set "MOD_ROOT=%OUT_DIR_ABS%\mod"
set "FULL_PAYLOAD=%FULL_ROOT%\fahrenheit"
set "MOD_PAYLOAD=%MOD_ROOT%\%MOD_ID%"
set "FULL_OUT=%OUT_DIR_ABS%\fahrenheit-full-%TAG%.zip"
set "MOD_OUT=%OUT_DIR_ABS%\%MOD_ID%-mod-%TAG%.zip"
set "FULL_SHA=%FULL_OUT%.sha256"
set "MOD_SHA=%MOD_OUT%.sha256"

if not exist "%DEPLOY_DIR_ABS%" (
    echo ERROR: deploy directory not found: "%DEPLOY_DIR_ABS%"
    exit /B %EXIT_RUNTIME%
)
if not exist "%MOD_DEPLOY_DIR%" (
    echo ERROR: mod deploy directory not found: "%MOD_DEPLOY_DIR%"
    exit /B %EXIT_RUNTIME%
)

if not exist "%FULL_PAYLOAD%" mkdir "%FULL_PAYLOAD%" >NUL 2>&1
if not exist "%MOD_PAYLOAD%" mkdir "%MOD_PAYLOAD%" >NUL 2>&1

robocopy "%DEPLOY_DIR_ABS%" "%FULL_PAYLOAD%" /E /NFL /NDL /NJH /NJS /NP >NUL
if %ERRORLEVEL% GEQ 8 (
    echo ERROR: robocopy failed for full package with code %ERRORLEVEL%.
    exit /B %EXIT_RUNTIME%
)

robocopy "%MOD_DEPLOY_DIR%" "%MOD_PAYLOAD%" /E /NFL /NDL /NJH /NJS /NP >NUL
if %ERRORLEVEL% GEQ 8 (
    echo ERROR: robocopy failed for mod package with code %ERRORLEVEL%.
    exit /B %EXIT_RUNTIME%
)

if exist "%FULL_OUT%" del /F /Q "%FULL_OUT%" >NUL 2>&1
if exist "%MOD_OUT%" del /F /Q "%MOD_OUT%" >NUL 2>&1
if exist "%FULL_SHA%" del /F /Q "%FULL_SHA%" >NUL 2>&1
if exist "%MOD_SHA%" del /F /Q "%MOD_SHA%" >NUL 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%FULL_PAYLOAD%' -DestinationPath '%FULL_OUT%' -Force"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: failed to create "%FULL_OUT%".
    exit /B %EXIT_RUNTIME%
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%MOD_PAYLOAD%' -DestinationPath '%MOD_OUT%' -Force"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: failed to create "%MOD_OUT%".
    exit /B %EXIT_RUNTIME%
)

call :write_sha256 "%FULL_OUT%" "%FULL_SHA%"
if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%
call :write_sha256 "%MOD_OUT%" "%MOD_SHA%"
if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%

echo Package output:
echo   %FULL_OUT%
echo   %MOD_OUT%
echo   %FULL_SHA%
echo   %MOD_SHA%
exit /B 0

:resolve_path
setlocal
set "P=%~1"
if "%P%"=="" (
    endlocal & set "%~2="
    exit /B 0
)
if "%P:~1,1%"==":" (
    for %%I in ("%P%") do endlocal & set "%~2=%%~fI" & exit /B 0
)
if "%P:~0,2%"=="\\" (
    for %%I in ("%P%") do endlocal & set "%~2=%%~fI" & exit /B 0
)
for %%I in ("%REPO_ROOT%\%P%") do endlocal & set "%~2=%%~fI" & exit /B 0

:write_sha256
setlocal EnableDelayedExpansion
set "FILE=%~1"
set "OUT=%~2"
set "HASH_LINE="
set "HASH="
set "BASE="

if not exist "!FILE!" (
    echo ERROR: checksum source not found: "!FILE!"
    endlocal & exit /B %EXIT_RUNTIME%
)

for %%I in ("!FILE!") do set "BASE=%%~nxI"
for /F "usebackq delims=" %%H in (`certutil -hashfile "!FILE!" SHA256 ^| findstr /R /I "^[0-9A-F][0-9A-F]*$"`) do (
    if not defined HASH_LINE set "HASH_LINE=%%H"
)
if not defined HASH_LINE (
    echo ERROR: failed to compute checksum for "!FILE!".
    endlocal & exit /B %EXIT_RUNTIME%
)
set "HASH=!HASH_LINE!"

> "!OUT!" echo !HASH!  !BASE!
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: failed to write checksum file "!OUT!".
    endlocal & exit /B %EXIT_RUNTIME%
)

endlocal & exit /B 0

:print_usage
echo Usage: scripts\package-release.cmd --tag vX.Y.Z [--deploy-dir path] [--mod-id fhparry] [--out-dir .release]
echo.
echo Example:
echo   scripts\package-release.cmd --tag v1.2.3 --deploy-dir ".workspace\fahrenheit\artifacts\deploy\rel" --mod-id fhparry --out-dir .release
exit /B 0
