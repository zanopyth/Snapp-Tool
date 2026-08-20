@echo off
cd /d "%~dp0"
echo Building self-contained standalone SnapTool.exe (no .NET runtime required on the target machine)...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "publish"
echo.
echo Done. Standalone exe: publish\SnapTool.exe
pause
