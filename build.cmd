@echo off
rem Asistente de compilacion: lanza build.ps1 sin depender de la ExecutionPolicy.
rem Uso: build            (pregunta todo)
rem      build -Platform win-x64 -Configuration Release -Test no -Zip yes -Yes
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
