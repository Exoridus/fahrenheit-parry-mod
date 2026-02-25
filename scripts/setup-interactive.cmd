@echo off
setlocal EnableExtensions

set "GAME_DIR_ARG=%~1"
set "CONFIGURATION=%~2"
set "FAHRENHEIT_REPO=%~3"
set "FAHRENHEIT_DIR=%~4"
set "NATIVE_MSBUILD_EXE=%~5"

if not defined CONFIGURATION set "CONFIGURATION=Debug"
if not defined FAHRENHEIT_REPO set "FAHRENHEIT_REPO=https://github.com/peppy-enterprises/fahrenheit.git"
if not defined FAHRENHEIT_DIR set "FAHRENHEIT_DIR=.workspace/fahrenheit"

echo.
set "DO_SETUP_DIR=Y"
set /P DO_SETUP_DIR=Would you like to configure game deploy path now? [Y/n]:
if /I not "%DO_SETUP_DIR%"=="N" (
    cmd /C scripts\setup-game-dir.cmd "%GAME_DIR_ARG%"
    if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%
)

echo.
set "DO_FULL_BUILD=Y"
set /P DO_FULL_BUILD=Run first full build now? (Recommended) [Y/n]:
if /I "%DO_FULL_BUILD%"=="N" exit /B 0

if defined NATIVE_MSBUILD_EXE (
    dotnet msbuild build.proj -nologo -verbosity:minimal -t:Build -p:Configuration="%CONFIGURATION%" -p:FahrenheitRepo="%FAHRENHEIT_REPO%" -p:FahrenheitDir="%FAHRENHEIT_DIR%" -p:NativeMSBuildExe="%NATIVE_MSBUILD_EXE%"
) else (
    dotnet msbuild build.proj -nologo -verbosity:minimal -t:Build -p:Configuration="%CONFIGURATION%" -p:FahrenheitRepo="%FAHRENHEIT_REPO%" -p:FahrenheitDir="%FAHRENHEIT_DIR%"
)
exit /B %ERRORLEVEL%
