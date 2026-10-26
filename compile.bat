@echo off
rem Builds dist\poe2verlay.exe (self-contained, single file) and, when Inno Setup 6 is installed,
rem the installer dist\poe2verlay-setup-<version>.exe.
rem Usage: compile.bat [version]        e.g. compile.bat 1.0.1
setlocal
cd /d "%~dp0"
call :build %*
set "RC=%ERRORLEVEL%"
if not defined CI pause
exit /b %RC%

:build
where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET 8 SDK was not found. Install it with:
    echo   winget install Microsoft.DotNet.SDK.8
    echo or download it from https://dotnet.microsoft.com/download/dotnet/8.0
    exit /b 1
)

set "VERSIONARG="
if not "%~1"=="" set "VERSIONARG=-p:Version=%~1"
dotnet publish poe2verlay.csproj -c Release -o dist --nologo %VERSIONARG%
if errorlevel 1 exit /b 1

set "ISCC="
for %%I in (ISCC.exe) do set "ISCC=%%~$PATH:I"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC (
    echo.
    echo Built dist\poe2verlay.exe. To also build the installer, install Inno Setup 6:
    echo   winget install JRSoftware.InnoSetup
    exit /b 0
)

"%ISCC%" /Q installer\poe2verlay.iss
if errorlevel 1 exit /b 1
echo.
echo Built:
dir /b dist\*.exe
exit /b 0
