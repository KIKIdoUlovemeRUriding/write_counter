@echo off
cd /d "%~dp0"
if exist "publish\InkFlow.exe" (
    start "" "publish\InkFlow.exe"
    exit /b 0
)
echo InkFlow has not been published yet. Starting with dotnet run...
dotnet run --project "src\InkFlow\InkFlow.csproj"
