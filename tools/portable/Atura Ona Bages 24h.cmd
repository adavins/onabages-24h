@echo off
rem Atura Ona Bages 24h sencera (interficie i motor). Nomes per a un administrador (D-191).
rem Demana l'usuari i la contrasenya de l'administrador i confirmacio. L'aturada queda al registre.
title Atura Ona Bages 24h
cd /d "%~dp0"
echo Atura Ona Bages 24h
echo ===================
echo La radio es quedara en silenci fins que es torni a obrir OnaBages24.exe.
echo Si l'Inicialitzador vigila l'aplicacio, la tornara a obrir: pausa'l primer ("Pausa monitoritzacio").
echo.
"resources\engine\OnaBages24.Engine.exe" atura
echo.
pause
