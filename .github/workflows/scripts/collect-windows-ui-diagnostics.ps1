$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force TestResults | Out-Null
$events = @(Get-WinEvent -FilterHashtable @{
    LogName = 'Application'
    StartTime = (Get-Date).AddMinutes(-30)
} -ErrorAction SilentlyContinue | Where-Object {
    $_.ProviderName -in @('.NET Runtime', 'Application Error', 'Windows Error Reporting') -and
    $_.Message -match 'ExampleAShellApp|AdaptiveShell|Microsoft\.UI\.Xaml'
} | Select-Object -First 200 TimeCreated, ProviderName, Id, LevelDisplayName, Message)
$processes = @(Get-Process ExampleAShellApp -ErrorAction SilentlyContinue |
    Select-Object Id, ProcessName, MainWindowHandle, StartTime)
@{
    capturedAt = (Get-Date).ToUniversalTime().ToString('o')
    events = $events
    processes = $processes
} | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 TestResults/windows-app-events.json
