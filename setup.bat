@echo off
setlocal
title Volcanoids Direct Connect - setup
rem Usage:
rem   setup.bat                        asks where Volcanoids is installed (suggests the folder it finds),
rem                                    installs BepInEx + the mod and makes the game ready to play
rem   setup.bat "D:\Games\Volcanoids"  same, but uses this folder (you can also drag the game
rem                                    folder onto setup.bat)
rem   setup.bat uninstall              remove the mod

set "ARGS="
if /I "%~1"=="uninstall" (
    set "ARGS=-Uninstall"
) else if not "%~1"=="" (
    set "ARGS=-GamePath "%~1""
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\setup.ps1" %ARGS%
exit /b %ERRORLEVEL%
