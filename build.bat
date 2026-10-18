@echo off
setlocal
cd /d "%~dp0"

set "OUT=MTR-Update-Tool.exe"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "LOG=%~dp0build.log"

echo Working folder: %CD%
echo.

if not exist "%CSC%" (
  echo ERROR: 64-bit .NET Framework 4 C# compiler not found at:
  echo   %CSC%
  goto :fail
)
if not exist "Program.cs" (
  echo ERROR: Program.cs not found in this folder.
  goto :fail
)
if not exist "app.ico" (
  echo ERROR: app.ico not found in this folder.
  goto :fail
)
if not exist "logo.png" (
  echo ERROR: logo.png not found in this folder.
  goto :fail
)
if exist "app.manifest" goto :havemanifest
echo app.manifest not found - creating it...
> app.manifest echo ^<?xml version="1.0" encoding="utf-8"?^>
>> app.manifest echo ^<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1"^>
>> app.manifest echo   ^<assemblyIdentity version="1.0.0.0" name="MTRUpdateTool.app"/^>
>> app.manifest echo   ^<trustInfo xmlns="urn:schemas-microsoft-com:asm.v2"^>
>> app.manifest echo     ^<security^>
>> app.manifest echo       ^<requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3"^>
>> app.manifest echo         ^<requestedExecutionLevel level="requireAdministrator" uiAccess="false"/^>
>> app.manifest echo       ^</requestedPrivileges^>
>> app.manifest echo     ^</security^>
>> app.manifest echo   ^</trustInfo^>
>> app.manifest echo   ^<compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"^>
>> app.manifest echo     ^<application^>
>> app.manifest echo       ^<supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/^>
>> app.manifest echo     ^</application^>
>> app.manifest echo   ^</compatibility^>
>> app.manifest echo ^</assembly^>
:havemanifest

if exist "%OUT%" del "%OUT%"

echo Building %OUT% ...
echo.
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:"%OUT%" /win32manifest:app.manifest /win32icon:app.ico /reference:System.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /resource:logo.png,logo.png Program.cs > "%LOG%" 2>&1
set "RC=%ERRORLEVEL%"
type "%LOG%"
echo.

if not "%RC%"=="0" goto :fail
if not exist "%OUT%" goto :fail

echo ============================================
echo SUCCESS: %CD%\%OUT%
echo ============================================
echo.
pause
endlocal
exit /b 0

:fail
echo.
echo ============================================
echo BUILD FAILED  (details above and in build.log)
echo ============================================
echo.
pause
endlocal
exit /b 1
