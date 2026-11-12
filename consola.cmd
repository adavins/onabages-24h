@echo off
rem Consola de desenvolupament d'Ona Bages 24h: arrencar, aturar, compilar, provar, empaquetar i diagnosticar el motor.
rem Es pot obrir amb doble clic o des d'una consola a l'arrel del projecte. Cal Node.js 22 o posterior i el SDK de .NET 10.

cd /d "%~dp0"
chcp 65001 >nul
where node >nul 2>nul
if errorlevel 1 (
    echo No es troba Node.js. Cal instal.lar-lo ^(versio 22 o posterior^).
    pause
    exit /b 1
)
node tools\consola.mjs
