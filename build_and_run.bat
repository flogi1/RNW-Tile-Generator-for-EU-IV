@echo off
setlocal
cd /d "%~dp0"

REM Minimised re-launch, see run.bat.
if /i not "%~1"=="inner" (
    start "" /min cmd /c ""%~f0" inner"
    exit /b
)

REM See build.bat for why this clean is here: without it, dotnet can
REM silently reuse a stale RnwTileGenerator.Core.dll from a previous
REM delivered version and produce confusing "type does not exist" errors.
if exist "RnwTileGenerator.Core\bin" rmdir /s /q "RnwTileGenerator.Core\bin"
if exist "RnwTileGenerator.Core\obj" rmdir /s /q "RnwTileGenerator.Core\obj"
if exist "RnwTileGenerator.App\bin" rmdir /s /q "RnwTileGenerator.App\bin"
if exist "RnwTileGenerator.App\obj" rmdir /s /q "RnwTileGenerator.App\obj"
if exist "RnwTileGenerator.Updates\bin" rmdir /s /q "RnwTileGenerator.Updates\bin"
if exist "RnwTileGenerator.Updates\obj" rmdir /s /q "RnwTileGenerator.Updates\obj"
if exist "RnwTileGenerator.Checks\bin" rmdir /s /q "RnwTileGenerator.Checks\bin"
if exist "RnwTileGenerator.Checks\obj" rmdir /s /q "RnwTileGenerator.Checks\obj"

dotnet build RnwTileGenerator.App > build_log.txt 2>&1
if errorlevel 1 (
    start "" notepad "build_log.txt"
    exit /b 1
)

start "" "RnwTileGenerator.App\bin\Debug\net10.0-windows\RnwTileGenerator.exe"
exit /b
