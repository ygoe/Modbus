@echo off

:: NOTES:
:: - Install necessary NuGet package "coverlet.collector" for a new test project
:: - Install VSCode extension "ryanluker.vscode-coverage-gutters" to see line coverage in the editor
::   (click the status bar button to show)

:: Initialise
pushd "%~dp0"

:: Run tests
dotnet test --collect:"XPlat Code Coverage"

:: Generate report
dotnet tool install -g dotnet-reportgenerator-globaltool || goto error
reportgenerator -reports:"**\coverage.cobertura.xml" -targetdir:artifacts\coveragereport -reporttypes:Html || goto error

:: Exit
powershell write-host -fore Green Report created.
popd
timeout /t 2 /nobreak >nul
exit /b

:error
pause
