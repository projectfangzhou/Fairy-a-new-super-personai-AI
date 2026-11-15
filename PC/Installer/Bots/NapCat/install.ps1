$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$zip = Join-Path $root 'NapCat.Windows.x64.zip'
$url = 'https://github.com/NapNeko/NapCatQQ/releases/latest/download/NapCat.Shell.zip'
Write-Host 'Downloading NapCat from the official release repository...'
Invoke-WebRequest -Uri $url -OutFile $zip
Expand-Archive -Path $zip -DestinationPath $root -Force
if (-not (Test-Path (Join-Path $root 'config.json'))) {
  Copy-Item (Join-Path $root 'config.template.json') (Join-Path $root 'config.json')
}
Write-Host 'NapCat files installed. Edit config.json, then run NapCat.exe and scan the QQ login QR code.'
