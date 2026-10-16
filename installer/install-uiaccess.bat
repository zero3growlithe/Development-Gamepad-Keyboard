@echo off
title Development Gamepad Keyboard - UIAccess installer (v1)
rem Installs the uiAccess build so the keyboard overlay floats ABOVE the Start menu.
rem This only works from the extracted app zip: uiaccess\ must sit next to this bat.
pushd "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-UiAccess.ps1"
popd
pause
