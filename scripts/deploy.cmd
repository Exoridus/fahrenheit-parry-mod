@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fI"
set "CONFIG_FILE=%REPO_ROOT%\.workspace\dev.local.mk"

set "GAME_DIR=%~1"
set "CONFIGURATION=%~2"
set "DEPLOY_MODE=%~3"
set "DEPLOY_CLEAN=%~4"
set "FAHRENHEIT_DIR=%~5"
set "MOD_ID=%~6"

if not defined CONFIGURATION set "CONFIGURATION=Debug"
if not defined DEPLOY_MODE set "DEPLOY_MODE=full"
if not defined DEPLOY_CLEAN set "DEPLOY_CLEAN=false"
if not defined FAHRENHEIT_DIR set "FAHRENHEIT_DIR=.workspace/fahrenheit"
if not defined MOD_ID set "MOD_ID=fhparry"

if not defined GAME_DIR call :read_config_game_dir
if not defined GAME_DIR (
    echo ERROR: GAME_DIR is not configured.
    echo Run "make setup-game-dir" first or pass GAME_DIR explicitly.
    exit /B 2
)

call :is_valid_game_dir "%GAME_DIR%"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Invalid GAME_DIR "%GAME_DIR%" ^(FFX.exe not found^).
    exit /B 2
)
for %%I in ("%GAME_DIR%") do set "GAME_DIR=%%~fI"

if /I "%CONFIGURATION%"=="Release" (
    set "DEPLOY_CFG=rel"
) else (
    set "DEPLOY_CFG=dbg"
)

set "SOURCE_ROOT=%REPO_ROOT%\%FAHRENHEIT_DIR%\artifacts\deploy\%DEPLOY_CFG%"
if not exist "%SOURCE_ROOT%" (
    echo ERROR: Build output not found: "%SOURCE_ROOT%"
    echo Run make build or make build-full first.
    exit /B 3
)

set "TARGET_ROOT=%GAME_DIR%\fahrenheit"
set "SOURCE_MODE="
set "TARGET_MODE="
set "ROBO_FLAGS=/E"

if /I "%DEPLOY_CLEAN%"=="true" set "ROBO_FLAGS=/MIR"

if /I "%DEPLOY_MODE%"=="full" (
    set "SOURCE_MODE=%SOURCE_ROOT%"
    set "TARGET_MODE=%TARGET_ROOT%"
) else if /I "%DEPLOY_MODE%"=="mod" (
    set "SOURCE_MODE=%SOURCE_ROOT%\mods\%MOD_ID%"
    set "TARGET_MODE=%TARGET_ROOT%\mods\%MOD_ID%"
) else (
    echo ERROR: Unknown deploy mode "%DEPLOY_MODE%". Use "full" or "mod".
    exit /B 2
)

if not exist "%SOURCE_MODE%" (
    echo ERROR: Source path does not exist: "%SOURCE_MODE%"
    exit /B 3
)

if not exist "%TARGET_ROOT%" mkdir "%TARGET_ROOT%" >NUL 2>&1

echo Deploying "%DEPLOY_MODE%" from:
echo   %SOURCE_MODE%
echo to:
echo   %TARGET_MODE%
echo Mode clean=%DEPLOY_CLEAN%, configuration=%CONFIGURATION%

robocopy "%SOURCE_MODE%" "%TARGET_MODE%" %ROBO_FLAGS% /NFL /NDL /NJH /NJS /NP >NUL
set "ROBO_RC=%ERRORLEVEL%"
if %ROBO_RC% GEQ 8 (
    echo ERROR: robocopy failed with code %ROBO_RC%.
    exit /B %ROBO_RC%
)

call :ensure_loadorder "%TARGET_ROOT%\mods\loadorder" "%MOD_ID%"
echo Deploy completed.
exit /B 0

:read_config_game_dir
if not exist "%CONFIG_FILE%" exit /B 0
set "TMP="
for /F "usebackq tokens=1,* delims==" %%A in (`findstr /B /I "GAME_DIR" "%CONFIG_FILE%"`) do (
    set "TMP=%%B"
)
if defined TMP (
    for /F "tokens=* delims= " %%Z in ("!TMP!") do set "GAME_DIR=%%Z"
)
exit /B 0

:ensure_loadorder
set "LOADORDER_FILE=%~1"
set "LOADORDER_ENTRY=%~2"
if not exist "%~dp1" mkdir "%~dp1" >NUL 2>&1

if not exist "%LOADORDER_FILE%" (
    > "%LOADORDER_FILE%" echo %LOADORDER_ENTRY%
    exit /B 0
)

findstr /I /X /C:"%LOADORDER_ENTRY%" "%LOADORDER_FILE%" >NUL
if %ERRORLEVEL% NEQ 0 (
    >> "%LOADORDER_FILE%" echo %LOADORDER_ENTRY%
)
exit /B 0

:is_valid_game_dir
if not exist "%~1" exit /B 1
if exist "%~1\FFX.exe" exit /B 0
exit /B 1
