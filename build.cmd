@echo off
rem OyunMonitor.exe derlemesi: Windows .NET Framework 4.8 derleyicisi (ek kurulum gerekmez).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /codepage:65001 /win32manifest:app.manifest /win32icon:ikon.ico /out:OyunMonitor.exe OyunMonitor.cs
