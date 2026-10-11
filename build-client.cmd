@echo off

:: ----- Build ModbusClientDemo -----

:: Initialise
pushd "%~dp0"

:: Clean
if exist artifacts rd /s /q artifacts || goto error
dotnet clean -nologo --tl -v m -c release || goto error

:: Build
pushd ModbusClientDemo
powershell write-host -fore Blue Building and publishing ModbusClientDemo for win-x64...
dotnet publish -c release -r win-x64 --self-contained -nologo || goto error

powershell write-host -fore Blue Building and publishing ModbusClientDemo for linux-arm...
dotnet publish -c release -r linux-arm --self-contained -nologo || goto error

powershell write-host -fore Blue Building and publishing ModbusClientDemo for linux-arm64...
dotnet publish -c release -r linux-arm64 --self-contained -nologo || goto error
popd

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
