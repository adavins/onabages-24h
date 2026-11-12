@echo off
rem Arrenca Ona Bages 24h en mode de desenvolupament (motor + interficie). El crida consola.cmd, opcio 1.
rem 1. Compila el motor (perque es faci servir sempre l'ultima versio).
rem 2. Obre la interficie (npm start, des de la carpeta app). Si no hi ha cap motor en marxa, l'arrenca ella.

cd /d "%~dp0.."

echo Compilant el motor...
dotnet build engine\OnaBages24.Engine -v q -nologo
if errorlevel 1 (
    echo.
    echo AVIS: no s'ha pogut compilar el motor.
    echo Si el motor ja esta en marxa, els seus fitxers estan en us i no es poden substituir:
    echo la interficie fara servir el motor que ja funciona. Per tenir l'ultima versio,
    echo atura'l des de la consola, opcio 3, i torna-ho a provar.
    echo.
)

cd app
if not exist node_modules (
    echo Instal.lant les dependencies de la interficie...
    call npm install
)
echo Obrint la interficie (aquesta finestra es tanca quan es tanqui la interficie)...
call npm start
