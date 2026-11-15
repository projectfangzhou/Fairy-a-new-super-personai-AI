$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$zip = Join-Path $root 'ComWeChatRobot.zip'
$url = 'https://github.com/ljc545w/ComWeChatRobot/releases/latest/download/ComWeChatRobot.zip'
Write-Host 'Downloading ComWeChatRobot from the official release repository...'
Invoke-WebRequest -Uri $url -OutFile $zip
Expand-Archive -Path $zip -DestinationPath $root -Force
Write-Host 'ComWeChatRobot files installed. Start WeChat, run the injector as administrator, and use the default local API port 19088.'
