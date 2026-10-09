@echo off

:: Initialise
pushd "%~dp0"

:: Clean
echo Deleting Visual Studio directory...
if exist .vs rd /s /q .vs || goto error

echo Deleting build directories...
if exist artifacts rd /s /q artifacts || goto error
dotnet clean -nologo --tl -v m -c Release || goto error
echo Done.

:: Exit
popd
timeout /t 2 /nobreak >nul
exit /b

:error
pause
