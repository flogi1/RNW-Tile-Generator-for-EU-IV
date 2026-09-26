@echo off
setlocal
cd /d "%~dp0"

echo Building RnwTileGenerator...

REM Force a clean rebuild every time. Without this, dotnet's incremental
REM build can decide an old bin/obj from a previous delivered version is
REM still "up to date" (it compares file timestamps, and files extracted
REM from a zip can end up looking older than a previous build's output),
REM silently reusing a stale RnwTileGenerator.Core.dll that is missing
REM whatever was most recently added there. That produces confusing
REM "type does not exist" errors even though the type is right there in
REM the .cs source. A clean avoids that class of problem entirely.
if exist "RnwTileGenerator.Core\bin" rmdir /s /q "RnwTileGenerator.Core\bin"
if exist "RnwTileGenerator.Core\obj" rmdir /s /q "RnwTileGenerator.Core\obj"
if exist "RnwTileGenerator.App\bin" rmdir /s /q "RnwTileGenerator.App\bin"
if exist "RnwTileGenerator.App\obj" rmdir /s /q "RnwTileGenerator.App\obj"
if exist "RnwTileGenerator.Updates\bin" rmdir /s /q "RnwTileGenerator.Updates\bin"
if exist "RnwTileGenerator.Updates\obj" rmdir /s /q "RnwTileGenerator.Updates\obj"
if exist "RnwTileGenerator.Checks\bin" rmdir /s /q "RnwTileGenerator.Checks\bin"
if exist "RnwTileGenerator.Checks\obj" rmdir /s /q "RnwTileGenerator.Checks\obj"

dotnet build RnwTileGenerator.App
if errorlevel 1 (
    echo.
    echo ===================================================
    echo  BUILD FAILED - see the errors above.
    echo ===================================================
    pause
    exit /b 1
)

echo.
echo Build succeeded.
pause
