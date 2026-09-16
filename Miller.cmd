@echo off
rem Root start file for Windows (Explorer double-click or cmd): runs the published binary from
rem dist\win-x64 and forwards every argument. Build first: bash build.sh
rem A command starting with -- (--version, --export) runs in this console and waits for the
rem result; anything else starts the window detached so this console closes at once.
set "APP=%~dp0dist\win-x64\Miller.exe"
if not exist "%APP%" (
  echo Miller.cmd: %APP% not found. Run "bash build.sh" first. 1>&2
  pause
  exit /b 1
)
if "%~1"=="" goto window
set "FIRST=%~1"
if "%FIRST:~0,2%"=="--" (
  "%APP%" %*
  exit /b
)
:window
start "" "%APP%" %*
