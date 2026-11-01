@echo off
setlocal
:: --root points at the sources on purpose: editing a module then reloading the page is the whole
:: development loop, and there is no build step to run in between.
dotnet run --project "%~dp0src\ALN.WebUi\ALN.WebUi.csproj" -- --root "%~dp0src\ALN.WebUi\wwwroot" %*
exit /b %ERRORLEVEL%
