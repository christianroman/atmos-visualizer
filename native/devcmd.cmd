@echo off
rem Runs one command inside the Visual Studio developer environment (cl, link, dumpbin, cmake,
rem ninja on PATH). Usage: devcmd.cmd dumpbin /exports build\Release\ear_shim.dll
setlocal
set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
set VSROOT=
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set VSROOT=%%i
if "%VSROOT%"=="" (
  echo No Visual Studio with the C++ tools found.
  exit /b 1
)
call "%VSROOT%\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64 -no_logo
if errorlevel 1 exit /b 1
%*
