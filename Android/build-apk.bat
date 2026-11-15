@echo off
setlocal

echo ========================================
echo FairyAI Android Build Script
echo ========================================

REM 设置环境变量
set USERNAME=l1763
set USERPROFILE=C:\Users\l1763
set NUGET_PACKAGES=C:\Users\l1763\.nuget\packages

echo.
echo 步骤 1: 清理项目
dotnet clean FairyAI-Android.csproj -c Release

echo.
echo 步骤 2: 还原 NuGet 包
dotnet restore FairyAI-Android.csproj

if errorlevel 1 (
    echo 还原失败！
    pause
    exit /b 1
)

echo.
echo 步骤 3: 构建 Release APK
dotnet build FairyAI-Android.csproj -c Release -f net10.0-android

if errorlevel 1 (
    echo 构建失败！
    pause
    exit /b 1
)

echo.
echo 步骤 4: 发布 APK
dotnet publish FairyAI-Android.csproj -c Release -f net10.0-android

if errorlevel 1 (
    echo 发布失败！
    pause
    exit /b 1
)

echo.
echo ========================================
echo 构建完成！
echo APK 位置: bin\Release\net10.0-android\
echo ========================================
pause
