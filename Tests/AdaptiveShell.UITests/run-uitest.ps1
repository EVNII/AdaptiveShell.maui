# 本地跑 E2E 测试(Windows):构建示例 App -> 启动 Appium -> dotnet test
#
# 用法:
#   ./run-uitest.ps1
#
# 前置: npm i -g appium; appium driver install windows; Windows 开发者模式开启
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
Set-Location $RepoRoot

$env:UITEST_PLATFORM = "windows"

# 确保 appium server 在跑
$appiumUp = $false
try {
    Invoke-RestMethod -Uri "http://127.0.0.1:4723/status" -TimeoutSec 2 | Out-Null
    $appiumUp = $true
} catch {}

if (-not $appiumUp) {
    Write-Host ">> Starting appium server (log: $env:TEMP\appium-uitest.log)"
    # npx 是 .cmd shim,Start-Process 不能直接起,要走 cmd.exe
    Start-Process cmd.exe -ArgumentList '/c',"npx --yes appium > `"$env:TEMP\appium-uitest.log`" 2>&1" `
        -WindowStyle Hidden
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        try {
            Invoke-RestMethod -Uri "http://127.0.0.1:4723/status" -TimeoutSec 2 | Out-Null
            break
        } catch {}
        if ($i -eq 59) { throw "appium server did not come up; see $env:TEMP\appium-uitest.log" }
    }
}

$Tfm = "net10.0-windows10.0.19041.0"
dotnet build Example/ExampleAShellApp/ExampleAShellApp.csproj -c Debug -f $Tfm

$exe = Get-ChildItem -Path "Example/ExampleAShellApp/bin/Debug/$Tfm" `
    -Filter "ExampleAShellApp.exe" -Recurse | Select-Object -First 1
if (-not $exe) { throw "ExampleAShellApp.exe not found under bin/Debug/$Tfm" }
$env:UITEST_APP_PATH = $exe.FullName

dotnet test Tests/AdaptiveShell.UITests/AdaptiveShell.UITests.csproj
