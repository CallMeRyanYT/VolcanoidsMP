@echo off
setlocal
title Volcanoids Direct Connect - setup
rem Usage:
rem   setup.bat                       install / update the mod (finds the game automatically)
rem   setup.bat "D:\Games\Volcanoids"  install into a specific game folder
rem   setup.bat uninstall             remove the mod

set "ARGS="
if /I "%~1"=="uninstall" (
    set "ARGS=-Uninstall"
) else if not "%~1"=="" (
    set "ARGS=-GamePath "%~1""
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\setup.ps1" %ARGS%
exit /b %ERRORLEVEL%
