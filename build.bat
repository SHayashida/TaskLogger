@echo off
setlocal
cd /d "%~dp0"

rem --- Find the C# compiler bundled with Windows (.NET Framework 4.x) ---
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo [ERROR] csc.exe not found. .NET Framework 4.x is required.
  pause
  exit /b 1
)

rem --- UI Automation assemblies ---
set "UIA=%FW%\WPF"
if not exist "%UIA%\UIAutomationClient.dll" set "UIA=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL"
if exist "%FW%\WPF\UIAutomationClient.dll" (
  set "R1=%FW%\WPF\UIAutomationClient.dll"
  set "R2=%FW%\WPF\UIAutomationTypes.dll"
  set "R3=%FW%\WPF\WindowsBase.dll"
) else (
  set "R1=%UIA%\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll"
  set "R2=%UIA%\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll"
  set "R3=%UIA%\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll"
)

echo Building TaskLogger.exe ...
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:TaskLogger.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
  /r:"%R1%" /r:"%R2%" /r:"%R3%" ^
  TaskLogger.cs
if errorlevel 1 (
  echo.
  echo [ERROR] Build failed.
  pause
  exit /b 1
)
echo.
echo Done: TaskLogger.exe
pause
