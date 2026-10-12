@echo off
rem Builds DummyToggle.exe with the C# compiler that ships with Windows. Nothing to install.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (echo Could not find %CSC% & pause & exit /b 1)
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0DummyToggle.exe" /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll /r:System.Core.dll "%~dp0DummyToggle.cs"
if errorlevel 1 (echo. & echo BUILD FAILED) else (echo Built DummyToggle.exe)
pause
