@echo off

:: ----- Build Unclassified.Modbus -----

:: Initialise
pushd "%~dp0"

:: Clean
if exist artifacts rd /s /q artifacts || goto error
dotnet clean -nologo --tl -v m -c release || goto error

:: Build
pushd Unclassified.Modbus
powershell write-host -fore Blue Building and packing Unclassified.Modbus...
dotnet build -c release -nologo || goto error
popd

:: ----- Finish -----

:: Exit
powershell write-host -fore Green Build finished.
popd
timeout /t 2 /nobreak >nul
exit /b

:error
pause
