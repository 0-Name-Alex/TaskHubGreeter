@echo off
rem Starts the Greeter against TaskHub's dev instance (..\TaskHub\.devhome, started by TaskHub's run-dev.cmd).
set TASKHUB_HOME=%~dp0..\TaskHub\.devhome
start "" "%~dp0src\TaskHubGreeter.App\bin\Debug\net10.0-windows\TaskHubGreeter.exe" %*
