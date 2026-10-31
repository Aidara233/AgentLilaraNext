@echo off
setlocal
set "DATA_DIR=E:\Tools\AgentLilaraNext\storage"
dotnet run --project "%~dp0src\ALN.Host\ALN.Host.csproj" -- --data-dir "%DATA_DIR%" %*
exit /b %ERRORLEVEL%
