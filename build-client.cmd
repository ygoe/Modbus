@echo off
set TargetFramework=net9.0

:: ----- Build ModbusClientDemo -----

:: Initialise
pushd "%~dp0"
cd ModbusClientDemo

:: Clean
if exist bin\Release\%TargetFramework% rd /s /q bin\Release\%TargetFramework% || goto error
dotnet clean -v m -c Release -nologo || goto error

:: Build
powershell write-host -fore Blue Building and publishing ModbusClientDemo for win-x64...
dotnet publish -c Release -r win-x64 --self-contained -nologo || goto error

powershell write-host -fore Blue Building and publishing ModbusClientDemo for linux-arm...
dotnet publish -c Release -r linux-arm --self-contained -nologo || goto error

powershell write-host -fore Blue Building and publishing ModbusClientDemo for linux-arm64...
dotnet publish -c Release -r linux-arm64 --self-contained -nologo || goto error

:: ----- Finish -----

:: Restore without runtime to clear errors in Visual Studio
powershell write-host -fore Blue Restoring solution packages...
dotnet restore -v q

:: Exit
powershell write-host -fore Green Build finished.
popd
timeout /t 2 /nobreak >nul
exit /b

:error
pause
