@echo off
rem Builds libear and ear_shim.dll, then copies the DLL into the Unity project
rem (UnityProject\Assets\Plugins\x86_64). Needs Visual Studio 2022 or later with the
rem "Desktop development with C++" workload; its CMake and Ninja are used, so nothing else.
rem The first run downloads the Boost headers libear needs (about 30 MB, checked by SHA-256).
rem Usage: build.cmd [Release|Debug]

setlocal
set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Release

rem find Visual Studio with the C++ tools (vswhere comes with every Visual Studio installer)
set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
if not exist "%VSWHERE%" (
  echo Visual Studio Installer not found. Install Visual Studio with "Desktop development with C++".
  exit /b 1
)
set VSROOT=
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set VSROOT=%%i
if "%VSROOT%"=="" (
  echo No Visual Studio with the C++ tools found. Add the "Desktop development with C++" workload.
  exit /b 1
)
call "%VSROOT%\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64 -no_logo
if errorlevel 1 exit /b 1

set CMAKE=%VSROOT%\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe
set NINJA=%VSROOT%\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe
set HERE=%~dp0
set BUILD=%HERE%build\%CONFIG%

"%CMAKE%" -S "%HERE%." -B "%BUILD%" -G Ninja -DCMAKE_MAKE_PROGRAM="%NINJA%" -DCMAKE_BUILD_TYPE=%CONFIG%
if errorlevel 1 exit /b 1

"%CMAKE%" --build "%BUILD%"
if errorlevel 1 exit /b 1

set PLUGINS=%HERE%..\UnityProject\Assets\Plugins\x86_64
copy /y "%BUILD%\ear_shim.dll" "%PLUGINS%\ear_shim.dll" >nul
if errorlevel 1 (
  echo Built %BUILD%\ear_shim.dll, but couldn't copy it into the Unity project - is Unity running?
  exit /b 1
)
echo Built %BUILD%\ear_shim.dll and copied it to UnityProject\Assets\Plugins\x86_64
