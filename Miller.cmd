@echo off
rem Root start file for Windows (Explorer double-click or cmd): runs the published binary from
rem dist\win-x64 and forwards every argument. Build first: bash build.sh
set "APP=%~dp0dist\win-x64\Miller.exe"
if not exist "%APP%" (
  echo Miller.cmd: %APP% not found. Run "bash build.sh" first. 1>&2
  pause
  exit /b 1
)
"%APP%" %*
