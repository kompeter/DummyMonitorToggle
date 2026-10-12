@echo off
rem Builds DummyToggle.exe with the C# compiler that ships with Windows. Nothing to install.
rem Pass "nopause" to skip the key press at the end (used by the automated build).
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (echo Could not find %CSC% & set RC=1 & goto done)
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0DummyToggle.exe" /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll /r:System.Core.dll "%~dp0DummyToggle.cs"
set RC=%ERRORLEVEL%
if "%RC%"=="0" (echo Built DummyToggle.exe) else (echo. & echo BUILD FAILED)
:done
if not "%1"=="nopause" pause
exit /b %RC%
