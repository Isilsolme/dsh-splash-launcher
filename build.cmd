@echo off
rem DSH GUI launcher build script (no external toolchain needed, uses Windows built-in csc.exe)
setlocal
set FW=C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ ^
  /win32icon:"%~dp0icons\whale-black.ico" ^
  /out:"%~dp0DSH-GUI.exe" ^
  /r:"%FW%\WPF\PresentationFramework.dll" ^
  /r:"%FW%\WPF\PresentationCore.dll" ^
  /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" ^
  /r:"%FW%\System.Management.dll" ^
  "%~dp0DSH-GUI.cs"
exit /b %errorlevel%
