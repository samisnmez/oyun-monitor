@echo off
rem OyunMonitor.exe derlemesi: Windows .NET Framework 4.8 derleyicisi (ek kurulum gerekmez).
rem Build OyunMonitor.exe with the .NET Framework 4.8 compiler that ships with Windows.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe bulunamadi / not found: %CSC%
  echo .NET Framework 4.8 gerekli / required.
  exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /platform:x64 /codepage:65001 /win32manifest:app.manifest /win32icon:ikon.ico /out:OyunMonitor.exe OyunMonitor.cs
if errorlevel 1 (
  echo Derleme basarisiz / Build failed. OyunMonitor.exe aciksa kapatin / close it if running.
  exit /b 1
)
echo OK: OyunMonitor.exe
