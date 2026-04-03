@echo off
setlocal EnableExtensions EnableDelayedExpansion

if /I "%~1"=="build" (
    set "DEFAULT_TARGET=Help"
    set "HELP_TARGET=Help"
    set "CLI_TARGET=Cli"
    set "LOCK_LABEL=build/tools"
    set "SMOKE_ONLY_VALUE=%BUILD_CMD_SMOKE_ONLY%"
    set "ALLOW_NESTED_VALUE=%BUILD_CMD_ALLOW_NESTED%"
    shift
    goto mode_ready
)

if /I "%~1"=="tools" (
    set "DEFAULT_TARGET=ToolsHelp"
    set "HELP_TARGET=ToolsHelp"
    set "CLI_TARGET=ToolsCli"
    set "LOCK_LABEL=build/tools"
    set "SMOKE_ONLY_VALUE=%TOOLS_CMD_SMOKE_ONLY%"
    set "ALLOW_NESTED_VALUE=%TOOLS_CMD_ALLOW_NESTED%"
    shift
    goto mode_ready
)

echo ERROR: missing or invalid launcher mode. Expected "build" or "tools".
exit /B 2

:mode_ready
call :ensure_dotnet
if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%

set "LOCKDIR="
set "LOCK_ENABLED=1"
if /I "%SMOKE_ONLY_VALUE%"=="1" set "LOCK_ENABLED=0"
if /I "%ALLOW_NESTED_VALUE%"=="1" set "LOCK_ENABLED=0"

if "%LOCK_ENABLED%"=="1" (
    call :acquire_lock
    if !ERRORLEVEL! NEQ 0 exit /B !ERRORLEVEL!
)

set "NUKE_ARGS="

if "%~1"=="" (
    set "NUKE_ARGS=--target %DEFAULT_TARGET%"
    goto run_nuke
)

set "CMD=%~1"

if /I "%CMD%"=="-h" goto global_help
if /I "%CMD%"=="--help" goto global_help
if /I "%CMD%"=="help" goto global_help

if /I "%CMD:~0,1%"=="-" (
    goto append_passthrough_args
)

if /I "%~2"=="-h" goto workflow_help
if /I "%~2"=="--help" goto workflow_help

set "NUKE_ARGS=--target %CLI_TARGET% --workflow %CMD%"
shift

:append_workflow_args
if "%~1"=="" goto run_nuke
set "NEXT_ARG=%~1"
if /I "%NEXT_ARG%"=="--target" set "NEXT_ARG=--build-target"
set "NUKE_ARGS=%NUKE_ARGS% %NEXT_ARG%"
shift
goto append_workflow_args

:append_passthrough_args
if "%~1"=="" goto run_nuke
if defined NUKE_ARGS (
    set "NUKE_ARGS=%NUKE_ARGS% %1"
) else (
    set "NUKE_ARGS=%1"
)
shift
goto append_passthrough_args

:global_help
if "%~2"=="" (
    set "NUKE_ARGS=--target %HELP_TARGET%"
) else (
    set "NUKE_ARGS=--target %HELP_TARGET% --workflow %~2"
)
goto run_nuke

:workflow_help
set "NUKE_ARGS=--target %HELP_TARGET% --workflow %CMD%"
goto run_nuke

:run_nuke
set "NUKE_TELEMETRY_OPTOUT=1"
echo [NUKE] dotnet run --project build\Build.csproj -- %NUKE_ARGS%
if /I "%SMOKE_ONLY_VALUE%"=="1" (
    if defined LOCKDIR if exist "%LOCKDIR%" rd /s /q "%LOCKDIR%" >NUL 2>&1
    exit /B 0
)

dotnet run --project build\Build.csproj -- %NUKE_ARGS%
set "EXIT_CODE=%ERRORLEVEL%"
if defined LOCKDIR if exist "%LOCKDIR%" rd /s /q "%LOCKDIR%" >NUL 2>&1
exit /B %EXIT_CODE%

:acquire_lock
if not exist ".nuke" mkdir ".nuke" >NUL 2>&1
set "LOCKDIR=.nuke\cli.lock"

if exist "%LOCKDIR%" (
    echo ERROR: another %LOCK_LABEL% invocation appears to be running, or a stale lock exists.
    echo If this is stale, remove "%LOCKDIR%" and retry.
    exit /B 9
)

mkdir "%LOCKDIR%" >NUL 2>&1
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: failed to create %LOCK_LABEL% lock directory "%LOCKDIR%".
    exit /B 9
)

>"%LOCKDIR%\started.txt" echo %DATE% %TIME%
exit /B 0

:ensure_dotnet
where dotnet >NUL 2>&1
if !ERRORLEVEL! EQU 0 (
    dotnet --list-sdks | findstr /R /C:"^10\." >NUL
    if !ERRORLEVEL! EQU 0 exit /B 0
)

where winget >NUL 2>&1
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: dotnet SDK 10.x missing and winget is unavailable.
    echo Install .NET SDK 10.x manually, then retry.
    exit /B 3
)

echo [MISSING] .NET SDK 10.x was not found.
echo This installation may require administrator privileges and can trigger a UAC prompt.
set "CONFIRM="
set /P CONFIRM=Install .NET SDK 10.x now? [y/N]:
if /I not "%CONFIRM%"=="Y" if /I not "%CONFIRM%"=="YES" (
    echo ERROR: installation declined.
    exit /B 3
)

echo Installing .NET SDK 10.x...
winget install --id "Microsoft.DotNet.SDK.10" -e --source winget --accept-source-agreements --accept-package-agreements --silent
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: failed to install .NET SDK 10.x.
    exit /B 4
)

where dotnet >NUL 2>&1
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: dotnet command still not available. Open a new terminal and retry.
    exit /B 4
)

dotnet --list-sdks | findstr /R /C:"^10\." >NUL
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: .NET SDK 10.x could not be verified after installation.
    exit /B 4
)

exit /B 0
