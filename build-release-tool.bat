@echo off
rem Builds the release tool to release-tool\RNW-Release.exe (see docs/superpowers/specs/2026-09-26-auto-update-design.md).
cd /d "%~dp0"
dotnet publish tools\RnwTileGenerator.Release -c Release -r win-x64 --self-contained false -o release-tool
if errorlevel 1 (pause & exit /b 1)
echo Done: release-tool\RNW-Release.exe
pause