@echo off
REM Runs the app from source. Needs the .NET 8 SDK; for a build with no prerequisites at all,
REM use scripts\publish.ps1 or grab the exe from the Releases page.
cd /d "%~dp0"
dotnet run --project src\TarkovMapCompanion -c Release %*
