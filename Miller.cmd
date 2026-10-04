@echo off
rem Root start file for Windows (Explorer double-click or cmd): runs the Release build from
rem src\Miller.App\bin\Release\net10.0, which every dotnet build refreshes, and forwards every
rem argument. Build first: dotnet build Miller.sln -c Release (bash build.sh does that as well).
rem A command starting with -- (--version, --export) runs in this console and waits for the
rem result; anything else starts the window detached so this console closes at once.
set "APP=%~dp0src\Miller.App\bin\Release\net10.0\Miller.exe"
if not exist "%APP%" (
  echo Miller.cmd: %APP% not found. Run "dotnet build Miller.sln -c Release" first. 1>&2
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
