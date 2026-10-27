@echo off
title Development Gamepad Keyboard - Drivers installer (v4)
rem Installs the optional features of Development Gamepad Keyboard:
rem   1) UIAccess             keyboard overlay above the Start menu
rem   2) Interception driver  keyboard input over Parsec (reboot needed)
rem   3) Both
rem Run this from the extracted app zip: the uiaccess\ folder must sit next to this file.
pushd "%~dp0"

echo.
echo  Development Gamepad Keyboard - Drivers installer
echo.
echo   1) UIAccess             keyboard overlay above the Start menu
echo   2) Interception driver  keyboard input over Parsec (reboot needed)
echo   3) Both
echo   Q) Quit
echo.

choice /c:123Q /n /m "Choose [1/2/3/Q]: "
if errorlevel 4 goto :quit
if errorlevel 3 goto :both
if errorlevel 2 goto :interception

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Drivers-installer.ps1" -UiAccess
goto :done

:interception
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Drivers-installer.ps1" -Interception
goto :done

:both
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Drivers-installer.ps1" -UiAccess -Interception
goto :done

:quit
echo Nothing selected.

:done
popd
pause
