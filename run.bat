@echo off
setlocal
cd /d "%~dp0"

REM The console window is only needed while building. Re-launch this script minimised
REM so nothing but a taskbar entry shows; the window closes as soon as the program starts.
if /i not "%~1"=="inner" (
    start "" /min cmd /c ""%~f0" inner"
    exit /b
)

set "EXE=RnwTileGenerator.App\bin\Debug\net10.0-windows\RnwTileGenerator.exe"

if not exist "%EXE%" (
    dotnet build RnwTileGenerator.App > build_log.txt 2>&1
    if errorlevel 1 (
        REM Show the errors in a normal window instead of a hidden console.
        start "" notepad "build_log.txt"
        exit /b 1
    )
)

if exist "%EXE%" (
    start "" "%EXE%"
) else (
    echo Could not find "%EXE%" even after building.> build_log.txt
    start "" notepad "build_log.txt"
)
exit /b
