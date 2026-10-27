@echo off
title Development Gamepad Keyboard - Driver uninstaller (v4)
rem Removes what Drivers-installer.bat installed:
rem   1) UIAccess             installed copy + Start-menu shortcut + signing certificate
rem   2) Interception driver  kernel driver + interception.dll copies (reboot needed)
rem   3) Both
rem The stock build in the app zip is untouched and keeps working.
pushd "%~dp0"

echo.
echo  Development Gamepad Keyboard - Driver uninstaller
echo.
echo   1) UIAccess             installed copy + shortcut + signing certificate
echo   2) Interception driver  kernel driver + dll copies (reboot needed)
echo   3) Both
echo   Q) Quit
echo.

choice /c:123Q /n /m "Choose [1/2/3/Q]: "
if errorlevel 4 goto :quit
if errorlevel 3 goto :both
if errorlevel 2 goto :interception

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Driver-uninstaller.ps1" -UiAccess
goto :done

:interception
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Driver-uninstaller.ps1" -Interception
goto :done

:both
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Driver-uninstaller.ps1" -UiAccess -Interception
goto :done

:quit
echo Nothing selected.

:done
popd
pause
