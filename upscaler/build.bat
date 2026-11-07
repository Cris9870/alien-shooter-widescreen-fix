@echo off
rem Builds d3d9.dll (32-bit) with the Visual Studio C++ tools.
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (echo No se encontro Visual Studio. & exit /b 1)
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (echo Falta el componente de C++ de Visual Studio. & exit /b 1)
call "%VSDIR%\VC\Auxiliary\Build\vcvars32.bat" >nul 2>&1
cd /d "%~dp0"
cl /nologo /O2 /W3 /MT /LD upscale.c /Fe:d3d9.dll /link /DEF:d3d9.def user32.lib kernel32.lib dxguid.lib
if errorlevel 1 exit /b 1
del /q upscale.obj d3d9.lib d3d9.exp 2>nul
echo d3d9.dll lista.
