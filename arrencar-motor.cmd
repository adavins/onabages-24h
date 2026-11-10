@echo off
rem Compila i arrenca nomes el motor d'Ona Bages 24h (sense interficie), en aquesta finestra.
rem Es veu el registre del motor. Ctrl+C l'atura (o aturar-motor.cmd des d'una altra finestra).
rem La interficie s'hi pot connectar despres amb arrencar.cmd o "npm start": troba el motor per dades\engine.json.

setlocal
cd /d "%~dp0"

echo Compilant el motor...
dotnet build engine\OnaBages24.Engine -v q -nologo
if errorlevel 1 (
    echo.
    echo ERROR: no s'ha pogut compilar el motor.
    echo Si ja n'hi ha un en marxa, atura'l primer amb aturar-motor.cmd.
    exit /b 1
)

set ENGINE=engine\OnaBages24.Engine\bin\Debug\net10.0-windows\OnaBages24.Engine.exe
echo.
echo Arrencant el motor (Ctrl+C per aturar-lo)...
"%ENGINE%" --data-dir "%~dp0dades"
