@echo off
setlocal EnableExtensions EnableDelayedExpansion

call :ensure_dotnet
if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%

set "LOCKDIR="
set "LOCK_ENABLED=1"
if /I "%BUILD_CMD_SMOKE_ONLY%"=="1" set "LOCK_ENABLED=0"
if /I "%BUILD_CMD_ALLOW_NESTED%"=="1" set "LOCK_ENABLED=0"

if "%LOCK_ENABLED%"=="1" (
    call :acquire_lock
    if !ERRORLEVEL! NEQ 0 exit /B !ERRORLEVEL!
)

if "%~1"=="" (
    set "NUKE_ARGS=--target Help"
    goto :run_nuke
)

set "CMD=%~1"

if /I "%CMD%"=="-h" goto :global_help
if /I "%CMD%"=="--help" goto :global_help
if /I "%CMD%"=="help" goto :global_help

if /I "%CMD:~0,1%"=="-" (
    set "NUKE_ARGS=%*"
    goto :run_nuke
)

if /I "%~2"=="-h" goto :workflow_help
if /I "%~2"=="--help" goto :workflow_help

set "NUKE_ARGS=--target Cli --workflow %CMD%"
:append_remaining_args
shift
if "%~1"=="" goto :run_nuke
set "NUKE_ARGS=%NUKE_ARGS% %1"
goto :append_remaining_args

:global_help
if "%~2"=="" (
    set "NUKE_ARGS=--target Help"
) else (
    set "NUKE_ARGS=--target Help --workflow %~2"
)
goto :run_nuke

:workflow_help
set "NUKE_ARGS=--target Help --workflow %CMD%"
goto :run_nuke

:run_nuke
set "NUKE_TELEMETRY_OPTOUT=1"
echo [NUKE] dotnet run --project build\Build.csproj -- %NUKE_ARGS%
if /I "%BUILD_CMD_SMOKE_ONLY%"=="1" (
    call :release_lock
    exit /B 0
)

dotnet run --project build\Build.csproj -- %NUKE_ARGS%
set "EXIT_CODE=%ERRORLEVEL%"
call :release_lock
exit /B %EXIT_CODE%

:acquire_lock
if not exist ".nuke" mkdir ".nuke" >NUL 2>&1
set "LOCKDIR=.nuke\run.lock"

if exist "%LOCKDIR%" (
    call :is_lock_active
    if !ERRORLEVEL! EQU 0 (
        echo ERROR: another build invocation appears to be running.
        echo If this is stale, remove "%LOCKDIR%" and retry.
        exit /B 9
    )

    echo [WARN] Stale build lock detected. Removing "%LOCKDIR%".
    rd /s /q "%LOCKDIR%" >NUL 2>&1
)

mkdir "%LOCKDIR%" >NUL 2>&1
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: failed to create build lock directory "%LOCKDIR%".
    exit /B 9
)

set "LOCK_OWNER_PID="
for /f %%P in ('powershell -NoProfile -Command "$PID"') do set "LOCK_OWNER_PID=%%P"
if defined LOCK_OWNER_PID (
    >"%LOCKDIR%\owner.pid" echo %LOCK_OWNER_PID%
)
>"%LOCKDIR%\started.txt" echo %DATE% %TIME%
exit /B 0

:is_lock_active
if not exist "%LOCKDIR%\owner.pid" exit /B 1

set "LOCK_OWNER_PID="
set /p LOCK_OWNER_PID=<"%LOCKDIR%\owner.pid"
if not defined LOCK_OWNER_PID exit /B 1

set "NON_NUMERIC_PID="
for /f "tokens=* delims=0123456789" %%A in ("%LOCK_OWNER_PID%") do set "NON_NUMERIC_PID=1"
if defined NON_NUMERIC_PID exit /B 1

tasklist /FI "PID eq %LOCK_OWNER_PID%" | findstr /R /C:"[ ]%LOCK_OWNER_PID%[ ]" >NUL
if !ERRORLEVEL! EQU 0 exit /B 0
exit /B 1

:release_lock
if defined LOCKDIR (
    if exist "%LOCKDIR%" rd /s /q "%LOCKDIR%" >NUL 2>&1
)
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
