@echo off
setlocal EnableExtensions EnableDelayedExpansion

call :ensure_dotnet
if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%

set "FORWARD_ARGS=%*"
if "%~1"=="" (
    set "NUKE_ARGS=--target Help"
) else (
    set "FIRST=%~1"
    if /I "!FIRST:~0,1!"=="-" (
        set "NUKE_ARGS=%FORWARD_ARGS%"
    ) else (
        set "NUKE_ARGS=--target %FORWARD_ARGS%"
    )
)

set "NUKE_TELEMETRY_OPTOUT=1"
echo [NUKE] dotnet run --project build\Build.csproj -- !NUKE_ARGS!
dotnet run --project build\Build.csproj -- !NUKE_ARGS!
exit /B %ERRORLEVEL%

:ensure_dotnet
where dotnet >NUL 2>&1
if %ERRORLEVEL% EQU 0 (
    dotnet --list-sdks | findstr /R /C:"^10\." >NUL
    if %ERRORLEVEL% EQU 0 exit /B 0
)

where winget >NUL 2>&1
if %ERRORLEVEL% NEQ 0 (
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
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: failed to install .NET SDK 10.x.
    exit /B 4
)

where dotnet >NUL 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: dotnet command still not available. Open a new terminal and retry.
    exit /B 4
)

dotnet --list-sdks | findstr /R /C:"^10\." >NUL
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: .NET SDK 10.x could not be verified after installation.
    exit /B 4
)

exit /B 0
