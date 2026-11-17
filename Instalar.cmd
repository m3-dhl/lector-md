@echo off
rem Instala Lector MD para el usuario actual (sin permisos de administrador).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
pause
