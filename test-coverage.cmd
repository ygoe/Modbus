@echo off

:: Initialise
pushd "%~dp0"

:: Run tests
:: Needs NuGet package "coverlet.collector" in the test project
dotnet test --collect:"XPlat Code Coverage"

:: Generate report
dotnet tool install -g dotnet-reportgenerator-globaltool || goto error
reportgenerator -reports:"**\coverage.cobertura.xml" -targetdir:artifacts\coveragereport -reporttypes:Html || goto error

:: Append style overrides
:: Special characters must be escaped: ^>, ^&, ^|, ^)
(
	echo.
	echo /* Overrides */
	echo .card-group .card table tr:hover { background-color: unset !important; }
	echo .overview tbody tr:hover^>td { background-color: unset !important; }
) >> artifacts\coveragereport\report.css || goto error

:: Exit
powershell write-host -fore Green Report created.
popd
timeout /t 2 /nobreak >nul
exit /b

:error
pause
