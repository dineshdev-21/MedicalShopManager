@echo off
setlocal enabledelayedexpansion

echo ========================================================
echo   Medical Shop Manager - Windows Executable Builder
echo ========================================================
echo.

:: 1. Check if dotnet CLI is installed
where dotnet >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo [ERROR] .NET SDK is not found on your system PATH!
    echo Please download and install .NET 8.0 SDK from:
    echo   https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

echo [*] Checking .NET version...
dotnet --version
echo.

:: 2. Set Paths
set "SOLUTION_DIR=%~dp0"
set "PROJECT_PATH=%SOLUTION_DIR%src\MedicalShopManager.App\MedicalShopManager.App.csproj"
set "OUTPUT_DIR=%SOLUTION_DIR%dist\publish"

echo [*] Project: %PROJECT_PATH%
echo [*] Output : %OUTPUT_DIR%
echo.

:: 3. Clean previous publish folder
if exist "%OUTPUT_DIR%" (
    echo [*] Cleaning previous build output...
    rmdir /s /q "%OUTPUT_DIR%"
)

:: 4. Run Tests (Optional check)
echo [*] Running unit tests...
dotnet test "%SOLUTION_DIR%MedicalShopManager.sln" -c Release --verbosity minimal
if %ERRORLEVEL% neq 0 (
    echo [WARNING] Some tests failed or had issues. Proceeding with publish...
)

:: 5. Publish Self-Contained Single-File Executable
echo.
echo [*] Publishing self-contained single-file win-x64 executable...
echo [*] (This embeds the .NET runtime, WPF libraries, and SQLite engine)
echo.

dotnet publish "%PROJECT_PATH%" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:PublishReadyToRun=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUTPUT_DIR%"

if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] Build failed! Check the error output above.
    echo.
    pause
    exit /b %ERRORLEVEL%
)

:: 6. Verify executable creation
if exist "%OUTPUT_DIR%\MedicalShopManager.exe" (
    echo.
    echo ========================================================
    echo   BUILD SUCCESSFUL!
    echo ========================================================
    echo.
    echo   Executable generated at:
    echo   %OUTPUT_DIR%\MedicalShopManager.exe
    echo.
    echo   You can run this .exe on any 64-bit Windows 10/11 machine.
    echo   No .NET runtime or prerequisites need to be installed!
    echo ========================================================
    echo.
    
    :: Copy README to output directory for convenience
    if exist "%SOLUTION_DIR%README.md" (
        copy "%SOLUTION_DIR%README.md" "%OUTPUT_DIR%\" >nul
    )
    
    :: Open File Explorer to the published output directory
    explorer.exe "%OUTPUT_DIR%"
) else (
    echo.
    echo [ERROR] MedicalShopManager.exe was not found in the output directory.
    echo.
)

pause
