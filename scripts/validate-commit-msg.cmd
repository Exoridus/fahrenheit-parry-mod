@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "EXIT_USAGE=2"
set "EXIT_INVALID=3"

set "MESSAGE_FILE="
set "SUBJECT="

if "%~1"=="" (
    call :print_usage
    exit /B %EXIT_USAGE%
)

if exist "%~1" (
    set "MESSAGE_FILE=%~1"
    shift
    goto args_done
)

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--message" (
    if "%~2"=="" (
        echo ERROR: --message requires a value.
        exit /B %EXIT_USAGE%
    )
    set "SUBJECT=%~2"
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

if defined MESSAGE_FILE if defined SUBJECT (
    echo ERROR: provide either a message file or --message, not both.
    exit /B %EXIT_USAGE%
)

if defined MESSAGE_FILE (
    if not exist "%MESSAGE_FILE%" (
        echo ERROR: message file not found: "%MESSAGE_FILE%"
        exit /B %EXIT_USAGE%
    )
    for /F "usebackq delims=" %%L in ("%MESSAGE_FILE%") do (
        if not defined SUBJECT (
            set "LINE=%%L"
            if defined LINE if not "!LINE:~0,1!"=="#" set "SUBJECT=!LINE!"
        )
    )
)

if not defined SUBJECT (
    echo ERROR: no commit subject found.
    exit /B %EXIT_INVALID%
)

call :validate "%SUBJECT%"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: invalid commit subject.
    echo Expected: type: message or type^(scope^): message
    echo Types: build, chore, ci, docs, feat, fix, perf, refactor, revert, style, test
    echo Examples:
    echo   feat: add parry indicator
    echo   fix^(ui^): align toggle labels
    exit /B %EXIT_INVALID%
)

exit /B 0

:validate
setlocal EnableDelayedExpansion
set "S=%~1"

if not defined S endlocal & exit /B 1

if /I "!S:~0,6!"=="Merge " endlocal & exit /B 0
if /I "!S:~0,7!"=="Revert " endlocal & exit /B 0
if /I "!S:~0,6!"=="fixup!" endlocal & exit /B 0
if /I "!S:~0,7!"=="squash!" endlocal & exit /B 0

call :matchtype build 5
if not errorlevel 1 endlocal & exit /B 0
call :matchtype chore 5
if not errorlevel 1 endlocal & exit /B 0
call :matchtype ci 2
if not errorlevel 1 endlocal & exit /B 0
call :matchtype docs 4
if not errorlevel 1 endlocal & exit /B 0
call :matchtype feat 4
if not errorlevel 1 endlocal & exit /B 0
call :matchtype fix 3
if not errorlevel 1 endlocal & exit /B 0
call :matchtype perf 4
if not errorlevel 1 endlocal & exit /B 0
call :matchtype refactor 8
if not errorlevel 1 endlocal & exit /B 0
call :matchtype revert 6
if not errorlevel 1 endlocal & exit /B 0
call :matchtype style 5
if not errorlevel 1 endlocal & exit /B 0
call :matchtype test 4
if not errorlevel 1 endlocal & exit /B 0
endlocal & exit /B 1

:matchtype
setlocal EnableDelayedExpansion
set "T=%~1"
set /A TLEN=%~2
set /A PLAIN_LEN=TLEN+2
set /A BREAK_LEN=TLEN+3
set /A OPEN_LEN=TLEN+1

if /I "!S:~0,%PLAIN_LEN%!"=="!T!: " if not "!S:~%PLAIN_LEN%!"=="" endlocal & exit /B 0
if /I "!S:~0,%BREAK_LEN%!"=="!T!!: " if not "!S:~%BREAK_LEN%!"=="" endlocal & exit /B 0
if /I "!S:~0,%OPEN_LEN%!"=="!T!(" echo(!S!| findstr /R /C:"): ." >NUL && endlocal & exit /B 0
if /I "!S:~0,%OPEN_LEN%!"=="!T!(" echo(!S!| findstr /R /C:")!: ." >NUL && endlocal & exit /B 0

endlocal & exit /B 1

:print_usage
echo Usage:
echo   scripts\validate-commit-msg.cmd COMMIT_MSG_FILE
echo   scripts\validate-commit-msg.cmd --message "type^(scope^): message"
exit /B 0
