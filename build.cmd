@echo off
setlocal EnableExtensions EnableDelayedExpansion

call :ensure_dotnet
if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%

if "%~1"=="" (
    set "NUKE_ARGS=--target Help"
    goto :run_nuke
)

set "CMD=%~1"
shift
set "REST="
:collect_rest
if "%~1"=="" goto :rest_done
set "REST=!REST! %~1"
shift
goto :collect_rest
:rest_done
if defined REST set "REST=!REST:~1!"
if defined REST (
    set "REST=!REST:--target =--payload !"
    set "REST=!REST:--target=--payload=!"
)

if /I "%CMD%"=="help" (
    if "%REST%"=="" (
        set "NUKE_ARGS=--target Help"
    ) else (
        set "NUKE_ARGS=--target Help --workflow %REST%"
    )
    goto :run_nuke
)

if /I "%CMD%"=="-h" (
    if "%REST%"=="" (
        set "NUKE_ARGS=--target Help"
    ) else (
        set "NUKE_ARGS=--target Help --workflow %REST%"
    )
    goto :run_nuke
)

if /I "%CMD%"=="--help" (
    if "%REST%"=="" (
        set "NUKE_ARGS=--target Help"
    ) else (
        set "NUKE_ARGS=--target Help --workflow %REST%"
    )
    goto :run_nuke
)

if /I "%CMD:~0,1%"=="-" (
    set "NUKE_ARGS=%CMD% %REST%"
    goto :run_nuke
)

set "TARGET="
if /I "%CMD%"=="install" set "TARGET=Install"
if /I "%CMD%"=="setup" set "TARGET=Setup"
if /I "%CMD%"=="clean" set "TARGET=Clean"
if /I "%CMD%"=="auto-deploy" set "TARGET=AutoDeploy"
if /I "%CMD%"=="doctor" set "TARGET=Doctor"
if /I "%CMD%"=="lint" set "TARGET=Lint"
if /I "%CMD%"=="smoke" set "TARGET=Smoke"
if /I "%CMD%"=="verify" set "TARGET=Verify"
if /I "%CMD%"=="build" set "TARGET=Build"
if /I "%CMD%"=="deploy" set "TARGET=Deploy"
if /I "%CMD%"=="start" set "TARGET=Start"
if /I "%CMD%"=="data-setup" set "TARGET=DataSetup"
if /I "%CMD%"=="ghidra-setup" set "TARGET=GhidraSetup"
if /I "%CMD%"=="ghidra-start" set "TARGET=GhidraStart"
if /I "%CMD%"=="data-extract" set "TARGET=DataExtract"
if /I "%CMD%"=="data-parse" set "TARGET=DataParse"
if /I "%CMD%"=="data-parse-all" set "TARGET=DataParseAll"
if /I "%CMD%"=="map-import" set "TARGET=MapImport"
if /I "%CMD%"=="map-build" set "TARGET=MapBuild"
if /I "%CMD%"=="data-inventory" set "TARGET=DataInventory"
if /I "%CMD%"=="data-offload" set "TARGET=DataOffload"
if /I "%CMD%"=="release-bump" set "TARGET=ReleaseBump"
if /I "%CMD%"=="release-ready" set "TARGET=ReleaseReady"
if /I "%CMD%"=="release-pack" set "TARGET=ReleasePack"
if /I "%CMD%"=="release-notes" set "TARGET=ReleaseNotes"
if /I "%CMD%"=="commit" set "TARGET=Commit"
if /I "%CMD%"=="commit-check" set "TARGET=CommitCheck"
if /I "%CMD%"=="commit-range" set "TARGET=CommitRange"

if not defined TARGET set "TARGET=%CMD%"

set "NUKE_ARGS=--target %TARGET%"
if not "%REST%"=="" set "NUKE_ARGS=!NUKE_ARGS! %REST%"

:run_nuke
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
