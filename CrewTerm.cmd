@echo off
setlocal
cd /d "%~dp0"
if exist "%~dp0artifacts\win-x64\crewterm.exe" (
  "%~dp0artifacts\win-x64\crewterm.exe" --project "%~dp0." %*
) else (
  dotnet run --project "%~dp0src\CrewTerm.App\CrewTerm.App.csproj" -- --project "%~dp0." %*
)
