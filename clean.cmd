@echo off

:: Initialise
pushd "%~dp0"

:: Clean
echo Deleting Visual Studio directory...
if exist .vs rd /s /q .vs || goto error

echo Deleting build directories...
if exist Unclassified.Modbus\bin rd /s /q Unclassified.Modbus\bin || goto error
if exist Unclassified.Modbus\obj rd /s /q Unclassified.Modbus\obj || goto error
if exist ModbusClientDemo\bin rd /s /q ModbusClientDemo\bin || goto error
if exist ModbusClientDemo\obj rd /s /q ModbusClientDemo\obj || goto error
if exist ModbusServerDemo\bin rd /s /q ModbusServerDemo\bin || goto error
if exist ModbusServerDemo\obj rd /s /q ModbusServerDemo\obj || goto error
echo Done.

:: Exit
popd
timeout /t 2 /nobreak >nul
exit /b

:error
pause
