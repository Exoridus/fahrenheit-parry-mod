@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "INSTALL_FULL=false"
set "DRY_RUN=false"

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="full" set "INSTALL_FULL=true"
if /I "%~1"=="--full" set "INSTALL_FULL=true"
if /I "%~1"=="--dry-run" set "DRY_RUN=true"
shift
goto parse_args

:args_done
echo ==========================================
echo Fahrenheit Parry Mod - prerequisite setup
echo install_full=%INSTALL_FULL%
echo dry_run=%DRY_RUN%
echo ==========================================

call :require_command winget
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: winget is required for this installer.
    echo Install App Installer from Microsoft Store, then re-run.
    exit /B 2
)

call :ensure_command git Git.Git "Git"
if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%

call :ensure_dotnet10
if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%

call :ensure_make
if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%

if /I "%INSTALL_FULL%"=="true" (
    call :ensure_full_native_toolchain
    if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%
)

echo.
echo Prerequisite check/install finished.
if /I "%INSTALL_FULL%"=="true" (
    echo Next steps:
    echo   1. Open a fresh terminal.
    echo   2. Run make setup
    echo   3. Run make build-full
) else (
    echo Next steps:
    echo   1. Run make setup
    echo   2. Run make build
)
exit /B 0

:require_command
where "%~1" >NUL 2>&1
if %ERRORLEVEL% EQU 0 exit /B 0
exit /B 1

:ensure_command
set "CMD_NAME=%~1"
set "PKG_ID=%~2"
set "LABEL=%~3"

call :require_command "%CMD_NAME%"
if !ERRORLEVEL! EQU 0 (
    echo [OK] %LABEL% is already installed.
    exit /B 0
)

echo [MISSING] %LABEL% not found. Installing via winget (%PKG_ID%)...
call :winget_install "%PKG_ID%"
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: failed to install %LABEL%.
    exit /B 10
)

call :require_command "%CMD_NAME%"
if !ERRORLEVEL! NEQ 0 (
    echo WARNING: %LABEL% install finished but command was not found on PATH yet.
    echo Open a new terminal and verify manually.
)
exit /B 0

:ensure_dotnet10
call :require_command dotnet
if !ERRORLEVEL! EQU 0 (
    dotnet --list-sdks | findstr /R /C:"^10\." >NUL
    if !ERRORLEVEL! EQU 0 (
        echo [OK] .NET SDK 10.x is already installed.
        exit /B 0
    )
)

echo [MISSING] .NET SDK 10.x not found. Installing via winget...
call :winget_install "Microsoft.DotNet.SDK.10"
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: failed to install .NET SDK 10.x.
    exit /B 11
)

call :require_command dotnet
if !ERRORLEVEL! NEQ 0 (
    echo WARNING: dotnet command still not found on PATH.
    echo Open a new terminal and verify manually.
    exit /B 0
)

dotnet --list-sdks | findstr /R /C:"^10\." >NUL
if !ERRORLEVEL! NEQ 0 (
    echo WARNING: .NET SDK 10.x was not detected after installation.
    echo Verify with: dotnet --list-sdks
)
exit /B 0

:ensure_make
where make >NUL 2>&1
if %ERRORLEVEL% EQU 0 (
    echo [OK] make is already installed.
    exit /B 0
)

echo [MISSING] make not found. Installing via winget (ezwinports.make)...
call :winget_install "ezwinports.make"
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: failed to install make.
    exit /B 12
)

where make >NUL 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo WARNING: make command still not found on PATH.
    echo You may need to open a new terminal.
)
exit /B 0

:ensure_full_native_toolchain
echo.
echo [FULL] Checking native full-build prerequisites...

where msbuild >NUL 2>&1
if %ERRORLEVEL% EQU 0 (
    echo [OK] msbuild command found.
) else (
    echo [MISSING] msbuild not found. Installing Visual Studio Build Tools via winget...
    call :winget_install_vs_buildtools
    if !ERRORLEVEL! NEQ 0 (
        echo ERROR: failed to install Visual Studio Build Tools.
        exit /B 13
    )
)

where vcpkg >NUL 2>&1
if %ERRORLEVEL% EQU 0 (
    echo [INFO] Running vcpkg integrate install...
    if /I "%DRY_RUN%"=="true" (
        echo [DRY-RUN] vcpkg integrate install
    ) else (
        vcpkg integrate install
    )
) else (
    echo [INFO] vcpkg command not on PATH.
    echo Run 'vcpkg integrate install' manually from a Developer PowerShell if needed.
)
exit /B 0

:winget_install
set "PKG=%~1"
if /I "%DRY_RUN%"=="true" (
    echo [DRY-RUN] winget install --id "%PKG%" -e --source winget --accept-source-agreements --accept-package-agreements
    exit /B 0
)
winget install --id "%PKG%" -e --source winget --accept-source-agreements --accept-package-agreements
exit /B %ERRORLEVEL%

:winget_install_vs_buildtools
if /I "%DRY_RUN%"=="true" (
    echo [DRY-RUN] winget install --id "Microsoft.VisualStudio.2022.BuildTools" -e --source winget --accept-source-agreements --accept-package-agreements --override "--wait --quiet --norestart --nocache --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --add Microsoft.VisualStudio.Workload.VCTools"
    exit /B 0
)
winget install --id "Microsoft.VisualStudio.2022.BuildTools" -e --source winget --accept-source-agreements --accept-package-agreements --override "--wait --quiet --norestart --nocache --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --add Microsoft.VisualStudio.Workload.VCTools"
exit /B %ERRORLEVEL%
