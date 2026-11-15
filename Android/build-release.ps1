<#
  Builds a signed release APK.
  Signing material lives outside the repository; this script only injects it into
  the build environment so nothing secret is ever committed to the csproj.

  Usage:  .\build-release.ps1 [-Configuration Release] [-KeyDirectory <dir>]
#>
param(
    [string]$Configuration = 'Release',
    [string]$KeyDirectory = (Join-Path $env:USERPROFILE '.fairyai-keys')
)

$ErrorActionPreference = 'Stop'

$keystore = Join-Path $KeyDirectory 'fairyai-release-3.0.jks'
$passwordFile = Join-Path $KeyDirectory 'android-signing-password.txt'

if (-not (Test-Path $keystore)) { throw "Keystore not found: $keystore" }
if (-not (Test-Path $passwordFile)) { throw "Password file not found: $passwordFile" }

$password = (Get-Content $passwordFile -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($password)) { throw "Password file is empty: $passwordFile" }

# The MSBuild/NuGet toolchain on some hosts ships with an empty ProgramFiles set,
# which makes NuGet.Configuration.ConfigurationDefaults throw on a null path.
$env:ProgramFiles = 'C:\Program Files'
Set-Item -Path Env:'ProgramFiles(x86)' -Value 'C:\Program Files (x86)'
$env:ProgramW6432 = 'C:\Program Files'
$env:CommonProgramFiles = 'C:\Program Files\Common Files'
Set-Item -Path Env:'CommonProgramFiles(x86)' -Value 'C:\Program Files (x86)\Common Files'

$env:ANDROID_KEYSTORE_PATH = $keystore
$env:ANDROID_KEYSTORE_PASSWORD = $password
$env:ANDROID_KEY_ALIAS = 'fairyai'
$env:ANDROID_KEY_PASSWORD = $password

$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
Push-Location $PSScriptRoot
try {
    & $dotnet build -c $Configuration -f net10.0-android
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

    $apk = Get-ChildItem -Path (Join-Path $PSScriptRoot "bin/$Configuration/net10.0-android") -Filter '*-Signed.apk' -Recurse -ErrorAction SilentlyContinue |
           Select-Object -First 1
    if ($apk) {
        Write-Host "Signed APK: $($apk.FullName)" -ForegroundColor Green
    } else {
        Write-Warning "Build succeeded but no *-Signed.apk was found."
    }
}
finally {
    Remove-Item Env:ANDROID_KEYSTORE_PATH, Env:ANDROID_KEYSTORE_PASSWORD, Env:ANDROID_KEY_ALIAS, Env:ANDROID_KEY_PASSWORD -ErrorAction SilentlyContinue
    Pop-Location
}
