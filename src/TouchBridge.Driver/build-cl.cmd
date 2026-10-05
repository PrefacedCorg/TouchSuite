@echo off
rem ============================================================================
rem  TouchBridge virtual touch screen driver - command line build (cl/link)
rem
rem  Does NOT require the Visual Studio "Windows Driver Kit" component.
rem  Headers, libs and tools all come from the WDK NuGet packages in packages\.
rem  Only the VS C++ toolchain (cl.exe / link.exe) is needed.
rem
rem  Output: x64\Release\TouchBridgeVhid.sys
rem  (.inf / .cat / signing / install are handled by build.ps1)
rem ============================================================================
setlocal enabledelayedexpansion
set HERE=%~dp0
set PKG=%HERE%packages
set WDKVER=10.0.28000.2526
set W=%PKG%\Microsoft.Windows.WDK.x64.%WDKVER%
set S=%PKG%\Microsoft.Windows.SDK.CPP.%WDKVER%
set KVER=1.15

if not exist "%W%\c\Include" (
  echo [ERROR] WDK NuGet package not found: %W%
  echo         Run: nuget restore packages.config -PackagesDirectory packages
  exit /b 1
)

set VCVARS=
for %%P in (
  "C:\Program Files\Microsoft Visual Studio\18\Community"
  "C:\Program Files\Microsoft Visual Studio\18\Professional"
  "C:\Program Files\Microsoft Visual Studio\18\Enterprise"
  "C:\Program Files\Microsoft Visual Studio\2022\Community"
  "C:\Program Files\Microsoft Visual Studio\2022\Professional"
  "C:\Program Files\Microsoft Visual Studio\2022\Enterprise"
  "C:\Program Files\Microsoft Visual Studio\2022\BuildTools"
) do (
  if exist "%%~P\VC\Auxiliary\Build\vcvars64.bat" set VCVARS=%%~P\VC\Auxiliary\Build\vcvars64.bat
)
if "%VCVARS%"=="" (
  echo [ERROR] vcvars64.bat not found. Install the VS "Desktop development with C++" workload.
  exit /b 1
)
call "%VCVARS%" >nul

set INCLUDES=/I"%W%\c\Include\10.0.28000.0\km" /I"%W%\c\Include\10.0.28000.0\km\crt" /I"%W%\c\Include\wdf\kmdf\%KVER%" /I"%W%\c\Include\10.0.28000.0\shared" /I"%S%\c\Include\10.0.28000.0\shared" /I"%S%\c\Include\10.0.28000.0\um" /I"%S%\c\Include\10.0.28000.0\ucrt"

set OUT=%HERE%x64\Release
set OBJ=%OUT%\obj
if not exist "%OBJ%" mkdir "%OBJ%"

cd /d "%HERE%"

echo == compile ==
cl /nologo /c /kernel /GS- /W4 /O2 /utf-8 /D_WIN64 /D_AMD64_ /DAMD64 /DPOOL_NX_OPTIN=1 /D_UNICODE /DUNICODE /DNDEBUG !INCLUDES! /Fo"%OBJ%\TouchBridgeVhid.obj" /Fd"%OBJ%\TouchBridgeVhid.pdb" TouchBridgeVhid.c
if errorlevel 1 exit /b 1

echo == link ==
link /nologo /DRIVER /SUBSYSTEM:NATIVE /ENTRY:FxDriverEntry /NODEFAULTLIB /INCREMENTAL:NO /OPT:REF /OPT:ICF /LIBPATH:"%W%\c\Lib\10.0.28000.0\km\x64" /LIBPATH:"%W%\c\Lib\wdf\kmdf\x64\%KVER%" /OUT:"%OUT%\TouchBridgeVhid.sys" "%OBJ%\TouchBridgeVhid.obj" wdfdriverentry.lib wdfldr.lib vhfkm.lib ntoskrnl.lib hal.lib BufferOverflowK.lib wmilib.lib
if errorlevel 1 exit /b 1

echo.
echo Build OK: %OUT%\TouchBridgeVhid.sys
endlocal