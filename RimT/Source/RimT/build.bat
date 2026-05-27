@echo off
echo [RimT] A compilar...

dotnet build RimT.csproj -c Release

IF %ERRORLEVEL% NEQ 0 (
    echo.
    echo [RimT] ERRO na compilacao. Ve os erros acima.
    pause
    exit /b 1
)

echo.
echo [RimT] Compilado com sucesso!
echo [RimT] O ficheiro RimT.dll esta em:
echo        %~dp0..\..\Assemblies\RimT.dll
echo.
echo [RimT] Copia a pasta RimT para:
echo        C:\Users\Guilherme Antonio\Documents\RimWorld.v1.6.4630\game\Mods\RimT
pause
