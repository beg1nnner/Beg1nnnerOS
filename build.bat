@echo off
setlocal
cd /d "%~dp0"
set VERSION=2.6.0

where dotnet >nul 2>nul || (echo .NET 8 SDK not found. Download: https://dotnet.microsoft.com/download/dotnet/8.0 & pause & exit /b 1)

echo [1/2] Building Beg1nnnerOS...
if exist publish rmdir /s /q publish
dotnet publish Beg1nnnerOS.csproj -c Release -r win-x64 --self-contained true -o publish
if errorlevel 1 (
  echo.
  echo BUILD FAILED. Copy the error lines above and send them to Claude.
  pause
  exit /b 1
)

echo.
echo [2/2] Building installer...
set ISCC=
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC (
  echo Inno Setup 6 not found - installer skipped.
  echo Install it from https://jrsoftware.org/isdl.php and run build.bat again.
  echo The program itself is ready: publish\Beg1nnnerOS.exe
  pause
  exit /b 0
)
"%ISCC%" /Qp /DAppVersion=%VERSION% installer\Beg1nnnerOS.iss
if errorlevel 1 (
  echo.
  echo INSTALLER FAILED. Copy the error lines above and send them to Claude.
  pause
  exit /b 1
)
echo.
echo Done: installer\Output\Beg1nnnerOS-Setup-%VERSION%.exe
explorer installer\Output
pause
