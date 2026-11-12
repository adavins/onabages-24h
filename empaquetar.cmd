@echo off
rem Genera la versio portable d'Ona Bages 24h (D-044):
rem   build\OnaBages24\              carpeta a punt per fer servir
rem   build\OnaBages24-<versio>.zip  el mateix, comprimit per distribuir-lo
rem
rem 1. Publica el motor amb el .NET inclos (no cal instal.lar-lo).
rem 2. Empaqueta la interficie amb @electron/packager i hi afegeix el motor (resources\engine).
rem 3. Hi posa la carpeta "dades" buida (mode portable) i les instruccions, i ho comprimeix.
rem
rem No s'hi copia res de la carpeta dades del repositori (base de dades, registres): la versio portable
rem comenca buida. Cal tenir tancada l'aplicacio empaquetada si funciona des de build\.

setlocal
cd /d "%~dp0"

for /f "delims=" %%v in ('node -p "require('./app/package.json').version"') do set VERSION=%%v
if "%VERSION%"=="" (
    echo ERROR: no s'ha pogut llegir la versio d'app\package.json. Cal tenir Node.js instal.lat.
    exit /b 1
)
echo Empaquetant Ona Bages 24h %VERSION%...

if exist build rmdir /s /q build
if exist build (
    echo ERROR: no s'ha pogut esborrar la carpeta build. Potser l'aplicacio empaquetada esta oberta.
    exit /b 1
)

echo.
echo [1/4] Publicant el motor...
rem Sense fitxers de depuracio (.pdb): no calen per fer servir l'aplicacio.
dotnet publish engine\OnaBages24.Engine -c Release -r win-x64 --self-contained -p:DebugType=none -o build\engine -v q -nologo
if errorlevel 1 goto :error

echo.
echo [2/4] Empaquetant la interficie...
rem Icona de l'executable, feta cada vegada a partir del logotip: si es canvia el logotip, la icona el segueix.
powershell -NoProfile -ExecutionPolicy Bypass -File tools\crea-icona.ps1 app\renderer\img\logo.png build\icona.ico
if errorlevel 1 goto :error
cd app
if not exist node_modules call npm install
rem Nomes el que necessita l'aplicacio: fora les proves.
call npx electron-packager . OnaBages24 --platform=win32 --arch=x64 --out=..\build --overwrite ^
    --extra-resource=..\build\engine ^
    --icon=..\build\icona.ico ^
    --ignore="^/tests" ^
    --app-version=%VERSION% ^
    --win32metadata.ProductName="Ona Bages 24h" ^
    --win32metadata.FileDescription="Ona Bages 24h" ^
    --win32metadata.CompanyName="Ona Bages"
if errorlevel 1 (
    cd ..
    goto :error
)
cd ..
move build\OnaBages24-win32-x64 build\OnaBages24 >nul
if errorlevel 1 goto :error

rem Idiomes de Chromium: nomes catala, castella i angles (la resta ocupa uns 45 MB i no es fa servir).
for %%f in (build\OnaBages24\locales\*.pak) do (
    if /i not "%%~nf"=="ca" if /i not "%%~nf"=="es" if /i not "%%~nf"=="en-US" del "%%f"
)

echo.
echo [3/4] Carpeta de dades i instruccions...
mkdir build\OnaBages24\dades
copy /y dades\LLEGEIX-ME.txt build\OnaBages24\dades\LLEGEIX-ME.txt >nul
if errorlevel 1 goto :error
copy /y tools\portable\LLEGEIX-ME.txt build\OnaBages24\LLEGEIX-ME.txt >nul
if errorlevel 1 goto :error

echo.
echo [4/4] Comprimint...
powershell -NoProfile -Command "Compress-Archive -Path 'build\OnaBages24' -DestinationPath 'build\OnaBages24-%VERSION%.zip' -Force"
if errorlevel 1 goto :error

echo.
echo Fet: build\OnaBages24-%VERSION%.zip
exit /b 0

:error
echo.
echo ERROR: no s'ha pogut empaquetar. Mira els missatges de sobre.
exit /b 1
