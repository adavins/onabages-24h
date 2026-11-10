@echo off
rem Atura el motor d'Ona Bages 24h que fa servir la carpeta dades del repositori (ordre engine.shutdown).
rem L'emissio s'atura. La interficie, si esta oberta, mostra "Sense connexio amb el motor".

cd /d "%~dp0"
node tools\prova-motor.mjs engine.shutdown
