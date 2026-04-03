@echo off
setlocal EnableExtensions EnableDelayedExpansion
call "%~dp0build\cli-launch.cmd" tools %*
exit /B %ERRORLEVEL%
