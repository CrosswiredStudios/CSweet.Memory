@echo off
setlocal EnableExtensions

pushd "%~dp0" || exit /b 1

set "SOLUTION=CSweet.Memory.slnx"
set "PACKAGE_VERSION=%~1"
set "OUTPUT_ROOT=%~2"

if not defined PACKAGE_VERSION (
    for /f "tokens=3 delims=<>" %%V in ('findstr /c:"<Version>" "Directory.Build.props"') do set "PACKAGE_VERSION=%%V"
)

if not defined PACKAGE_VERSION (
    echo Unable to read the package version from Directory.Build.props.
    goto :failed
)

if not defined OUTPUT_ROOT set "OUTPUT_ROOT=artifacts\packages"
set "OUTPUT_DIRECTORY=%OUTPUT_ROOT%\%PACKAGE_VERSION%"
for %%I in ("%OUTPUT_DIRECTORY%") do set "OUTPUT_DIRECTORY=%%~fI"

set "VERSION_PROPERTY=-p:Version=%PACKAGE_VERSION%"

echo Package version: %PACKAGE_VERSION%

if not exist "%OUTPUT_DIRECTORY%" (
    mkdir "%OUTPUT_DIRECTORY%" || goto :failed
)

echo.
echo Restoring published dependencies...
dotnet restore "%SOLUTION%" -p:UseLocalCSweetAgentSdk=false %VERSION_PROPERTY%
if errorlevel 1 goto :failed

echo.
echo Running tests...
dotnet test "%SOLUTION%" -c Release --no-restore -p:UseLocalCSweetAgentSdk=false %VERSION_PROPERTY%
if errorlevel 1 goto :failed

echo.
echo Creating NuGet packages in "%OUTPUT_DIRECTORY%"...
dotnet pack "%SOLUTION%" -c Release --no-restore -p:UseLocalCSweetAgentSdk=false %VERSION_PROPERTY% -o "%OUTPUT_DIRECTORY%"
if errorlevel 1 goto :failed

echo.
echo NuGet packages created successfully:
for %%P in ("%OUTPUT_DIRECTORY%\*.nupkg") do echo   %%~nxP

popd
exit /b 0

:failed
set "EXIT_CODE=%ERRORLEVEL%"
if "%EXIT_CODE%"=="0" set "EXIT_CODE=1"
echo.
echo Package creation failed with exit code %EXIT_CODE%.
popd
exit /b %EXIT_CODE%
