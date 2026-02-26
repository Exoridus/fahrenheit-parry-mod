@echo off
setlocal EnableExtensions

set "EXIT_USAGE=2"
set "EXIT_RUNTIME=5"

set "CONFIGURATION=Release"
set "FOUND_ANY=0"

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--configuration" (
    if "%~2"=="" (
        echo ERROR: --configuration requires a value.
        exit /B %EXIT_USAGE%
    )
    set "CONFIGURATION=%~2"
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

for /F "delims=" %%P in ('dir /S /B *test*.csproj 2^>NUL') do (
    echo %%P | findstr /I /C:".workspace" >NUL
    if ERRORLEVEL 1 (
        set "FOUND_ANY=1"
        echo Running tests for %%P
        dotnet test "%%P" --configuration "%CONFIGURATION%" --nologo
        if ERRORLEVEL 1 (
            echo ERROR: tests failed for %%P
            exit /B %EXIT_RUNTIME%
        )
    )
)

if "%FOUND_ANY%"=="0" (
    echo No test projects found outside .workspace. Skipping tests.
)

exit /B 0

:print_usage
echo Usage: scripts\run-tests-if-any.cmd [--configuration Debug^|Release]
echo.
echo Example:
echo   scripts\run-tests-if-any.cmd --configuration Release
exit /B 0
