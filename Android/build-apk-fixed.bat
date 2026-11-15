@echo off
setlocal

echo ========================================
echo FairyAI Android Build Script
echo ========================================

REM Set environment variables
set USERNAME=l1763
set USERPROFILE=C:\Users\l1763
set NUGET_PACKAGES=C:\Users\l1763\.nuget\packages
set DOTNET="C:\Program Files\dotnet\dotnet.exe"

echo.
echo Step 1: Clean project
%DOTNET% clean FairyAI-Android.csproj -c Release

echo.
echo Step 2: Restore NuGet packages
%DOTNET% restore FairyAI-Android.csproj

if errorlevel 1 (
    echo Restore failed!
    pause
    exit /b 1
)

echo.
echo Step 3: Build Release APK
%DOTNET% build FairyAI-Android.csproj -c Release -f net10.0-android

if errorlevel 1 (
    echo Build failed!
    pause
    exit /b 1
)

echo.
echo Step 4: Publish APK
%DOTNET% publish FairyAI-Android.csproj -c Release -f net10.0-android

if errorlevel 1 (
    echo Publish failed!
    pause
    exit /b 1
)

echo.
echo ========================================
echo Build complete!
echo APK location: bin\Release\net10.0-android\
echo ========================================
pause
