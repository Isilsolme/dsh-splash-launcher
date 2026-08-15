@echo off
rem DSH GUI launcher build script (no external toolchain needed, uses Windows built-in csc.exe)
rem splash.html / svg / png assets are embedded into the exe as resources:
rem the built DSH-GUI.exe is fully self-contained (portable single file).
rem Files with the same names next to the exe take precedence at runtime (custom splash).
setlocal
set FW=C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=C:\WINDOWS\Microsoft.NET\Framework\v4.0.30319
if not exist "%FW%\csc.exe" (
  echo csc.exe not found in .NET Framework 4.x. Install .NET Framework 4.x or adjust FW path.
  exit /b 1
)
"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ ^
  /win32icon:"%~dp0icons\whale-black.ico" ^
  /resource:"%~dp0splash.html",DshGui.splash.html ^
  /resource:"%~dp0deepseek-wordmark.svg",DshGui.deepseek-wordmark.svg ^
  /resource:"%~dp0whale-anim.svg",DshGui.whale-anim.svg ^
  /resource:"%~dp0whale.png",DshGui.whale.png ^
  /out:"%~dp0DSH-GUI.exe" ^
  /r:"%FW%\WPF\PresentationFramework.dll" ^
  /r:"%FW%\WPF\PresentationCore.dll" ^
  /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" ^
  /r:"%FW%\System.Management.dll" ^
  "%~dp0DSH-GUI.cs"
exit /b %errorlevel%
