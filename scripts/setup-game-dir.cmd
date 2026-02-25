@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fI"
set "CONFIG_FILE=%REPO_ROOT%\.workspace\dev.local.mk"
set "GAME_DIR_INPUT=%~1"
set "CANDIDATE="
set "FINAL_GAME_DIR="

if defined GAME_DIR_INPUT set "CANDIDATE=%GAME_DIR_INPUT%"
if not defined CANDIDATE call :read_config_candidate

if defined CANDIDATE (
    call :is_valid_game_dir "%CANDIDATE%"
    if !ERRORLEVEL! NEQ 0 (
        set "CANDIDATE="
    ) 
)

if not defined CANDIDATE call :detect_candidate

if defined CANDIDATE (
    call :is_valid_game_dir "%CANDIDATE%"
    if !ERRORLEVEL! EQU 0 (
        set "FINAL_GAME_DIR=!CANDIDATE!"
        echo Auto-detected game directory: "!FINAL_GAME_DIR!"
        goto write_config
    )
)

:prompt_loop
set "FINAL_GAME_DIR="
set /P FINAL_GAME_DIR=Enter the game install directory (must contain FFX.exe): 
if not defined FINAL_GAME_DIR (
    echo Path cannot be empty.
    goto prompt_loop
)
call :is_valid_game_dir "%FINAL_GAME_DIR%"
if !ERRORLEVEL! NEQ 0 (
    echo Invalid path: "%FINAL_GAME_DIR%"
    echo Expected to find FFX.exe in this directory.
    goto prompt_loop
)

:write_config
for %%I in ("%FINAL_GAME_DIR%") do set "FINAL_GAME_DIR=%%~fI"
if not exist "%REPO_ROOT%\.workspace" mkdir "%REPO_ROOT%\.workspace" >NUL 2>&1
> "%CONFIG_FILE%" (
    echo # Local developer configuration ^(auto-generated^)
    echo GAME_DIR = %FINAL_GAME_DIR%
)

echo Saved GAME_DIR to "%CONFIG_FILE%"
echo GAME_DIR=%FINAL_GAME_DIR%
exit /B 0

:read_config_candidate
if not exist "%CONFIG_FILE%" exit /B 0
for /F "usebackq tokens=1,* delims==" %%A in (`findstr /B /I "GAME_DIR" "%CONFIG_FILE%"`) do (
    set "TMP=%%B"
)
if defined TMP (
    for /F "tokens=* delims= " %%Z in ("!TMP!") do set "CANDIDATE=%%Z"
)
exit /B 0

:detect_candidate
call :check_candidate "C:\Games\Final Fantasy X-X2 - HD Remaster"
if defined CANDIDATE exit /B 0
call :check_candidate "C:\Games\Final Fantasy X_X-2 HD Remaster"
if defined CANDIDATE exit /B 0
call :check_candidate "C:\Games\FINAL FANTASY X_X-2 HD Remaster"
if defined CANDIDATE exit /B 0

if not defined CANDIDATE if defined ProgramFiles(x86) (
    call :check_candidate "%ProgramFiles(x86)%\Steam\steamapps\common\FINAL FANTASY X_X-2 HD Remaster"
)
if not defined CANDIDATE if defined ProgramFiles (
    call :check_candidate "%ProgramFiles%\Steam\steamapps\common\FINAL FANTASY X_X-2 HD Remaster"
)
if not defined CANDIDATE (
    call :check_candidate "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY X_X-2 HD Remaster"
)
if not defined CANDIDATE (
    call :check_candidate "C:\Program Files\Steam\steamapps\common\FINAL FANTASY X_X-2 HD Remaster"
)
for %%D in (D E F G H I J K L M N O P) do (
    if not defined CANDIDATE call :check_candidate "%%D:\SteamLibrary\steamapps\common\FINAL FANTASY X_X-2 HD Remaster"
    if not defined CANDIDATE call :check_candidate "%%D:\Games\Final Fantasy X-X2 - HD Remaster"
)
exit /B 0

:check_candidate
call :is_valid_game_dir "%~1"
if %ERRORLEVEL% EQU 0 set "CANDIDATE=%~1"
exit /B 0

:is_valid_game_dir
if not exist "%~1" exit /B 1
if exist "%~1\FFX.exe" exit /B 0
exit /B 1
