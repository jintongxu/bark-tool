param([string]$Fixture = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/BarkTool.Desktop/bin/Release/net10.0-windows/win-x64'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/BarkTool.Update.psm1') -Force
# Isolated process probe: never inspect/stop the user's actual running client.
& (Get-Module BarkTool.Update) { function script:Get-Process { param($Name, $ErrorAction) @() } }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('BarkUpdateTests-' + [guid]::NewGuid().ToString('N'))
$registry = 'HKCU:\Software\BarkTool.Update.Tests\' + [guid]::NewGuid().ToString('N')
$count = 0
function Assert($Condition, $Message) { if (-not $Condition) { throw $Message } }
function Reject([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert $rejected "Expected rejection: $Name"
    $script:count++; Write-Host "PASS: $Name"
}
function Seal([string]$Path) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Path 'BarkTool.dll')).FileVersion
    @{version=$version} | ConvertTo-Json | Set-Content (Join-Path $Path 'version.json')
    $files = @(Get-ChildItem $Path -Recurse -File | Where-Object Name -ne 'manifest.json' | ForEach-Object {
        @{path=$_.FullName.Substring($Path.Length + 1).Replace('\','/');sha256=(Get-BarkHash $_.FullName)}
    })
    @{schemaVersion=1;version=$version;runtime='win-x64';entryPoint='BarkTool.exe';files=$files} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Path 'manifest.json')
}
try {
    New-Item -ItemType Directory $temp | Out-Null
    New-Item -Path $registry -Force | Out-Null
    $package = Join-Path $temp 'package'
    New-Item -ItemType Directory $package | Out-Null
    foreach ($name in @('BarkTool.exe','BarkTool.dll')) { Copy-Item (Join-Path $Fixture $name) $package }
    'first' | Set-Content (Join-Path $package 'payload.txt'); Seal $package
    $ctx = @{Install=(Join-Path $temp 'Programs/BarkTool');Previous=(Join-Path $temp 'Programs/BarkTool.previous');Shortcut=(Join-Path $temp 'StartMenu/BarkTool.lnk');Registry=$registry;StartupApproved=($registry + '\Approved');BeforeActivate=$null}
    $data = Join-Path $temp 'BarkTool/settings.json'; New-Item -ItemType Directory (Split-Path $data) | Out-Null; 'keep' | Set-Content $data
    Invoke-BarkDeployment -Package $package -Context $ctx
    Assert (Test-Path $ctx.Shortcut) 'Shortcut missing'
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($ctx.Shortcut)
    Assert ($link.TargetPath -eq (Join-Path $ctx.Install 'BarkTool.exe')) 'Unstable shortcut'
    Assert (-not ((Get-Item $registry).GetValueNames() -contains 'BarkTool')) 'Autostart enabled without consent'
    $count++; Write-Host 'PASS: first install, stable shortcut, no autostart opt-in'
    Set-ItemProperty $registry -Name BarkTool -Value '"C:\old-portable\BarkTool.exe"'
    'second' | Set-Content (Join-Path $package 'payload.txt'); Seal $package
    Invoke-BarkDeployment -Package $package -Context $ctx
    Assert ((Get-Content (Join-Path $ctx.Previous 'payload.txt')) -eq 'first') 'Backup incorrect'
    Assert ((Get-ItemPropertyValue $registry BarkTool) -eq ('"' + (Join-Path $ctx.Install 'BarkTool.exe') + '"')) 'Autostart not migrated'
    $count++; Write-Host 'PASS: upgrade backup and existing autostart migration'
    & (Get-Module BarkTool.Update) { function script:Get-Process { param($Name, $ErrorAction) [pscustomobject]@{Name='BarkTool'} } }
    Reject { Invoke-BarkDeployment -Package $package -Context $ctx } 'running app refusal without termination'
    & (Get-Module BarkTool.Update) { function script:Get-Process { param($Name, $ErrorAction) @() } }
    $stream = [IO.File]::Open((Join-Path $ctx.Install 'BarkTool.exe'), 'Open','Read','None')
    try { Reject { Invoke-BarkDeployment -Package $package -Context $ctx } 'locked app refusal' } finally { $stream.Dispose() }
    $ctx.BeforeActivate = { throw 'Injected replacement failure' }
    Reject { Invoke-BarkDeployment -Package $package -Context $ctx } 'replacement failure rollback'
    Assert ((Get-Content (Join-Path $ctx.Install 'payload.txt')) -eq 'second') 'Install not restored'
    Assert ((Get-Content (Join-Path $ctx.Previous 'payload.txt')) -eq 'first') 'Previous not restored'
    $ctx.BeforeActivate = $null
    $realShortcut = $ctx.Shortcut
    'file' | Set-Content (Join-Path $temp 'not-a-directory')
    $ctx.Shortcut = Join-Path $temp 'not-a-directory/BarkTool.lnk'
    Reject { Invoke-BarkDeployment -Package $package -Context $ctx } 'shortcut failure restores activated payload'
    Assert ((Get-Content (Join-Path $ctx.Install 'payload.txt')) -eq 'second') 'Post-activation restoration failed'
    Assert ((Get-Content (Join-Path $ctx.Previous 'payload.txt')) -eq 'first') 'Previous backup lost on shortcut failure'
    $ctx.Shortcut = $realShortcut
    Invoke-BarkDeployment -Rollback -Context $ctx
    Assert ((Get-Content (Join-Path $ctx.Install 'payload.txt')) -eq 'first') 'Manual rollback incorrect'
    Assert ((Get-Content (Join-Path $ctx.Previous 'payload.txt')) -eq 'second') 'Rollback should retain displaced version'
    $count++; Write-Host 'PASS: manual rollback and reversible backup'
    New-Item $ctx.StartupApproved -Force | Out-Null
    New-ItemProperty $ctx.StartupApproved -Name BarkTool -Value ([byte[]]@(3,0,0,0)) -PropertyType Binary | Out-Null
    Set-ItemProperty $registry -Name BarkTool -Value 'disabled-original'
    Invoke-BarkDeployment -Package $package -Context $ctx
    Assert ((Get-ItemPropertyValue $registry BarkTool) -eq 'disabled-original') 'Disabled startup was migrated'
    $count++; Write-Host 'PASS: disabled StartupApproved entry not enabled or migrated'
    'corrupt' | Set-Content (Join-Path $ctx.Previous 'payload.txt')
    Reject { Invoke-BarkDeployment -Rollback -Context $ctx } 'corrupted rollback payload'
    'corrupt' | Set-Content (Join-Path $package 'payload.txt')
    Reject { Test-BarkPackage $package } 'hash mismatch'
    Seal $package
    'extra' | Set-Content (Join-Path $package 'extra.txt')
    Reject { Test-BarkPackage $package } 'unlisted payload'
    Remove-Item (Join-Path $package 'extra.txt')
    $manifest = Get-Content (Join-Path $package 'manifest.json') -Raw
    $bad = $manifest | ConvertFrom-Json; $bad.files[0].path = '../escape'
    $bad | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $package 'manifest.json')
    Reject { Test-BarkPackage $package } 'manifest traversal'
    $manifest | Set-Content (Join-Path $package 'manifest.json')
    $bad = $manifest | ConvertFrom-Json; $bad.version = '99.0.0.0'
    $bad | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $package 'manifest.json')
    Reject { Test-BarkPackage $package } 'version mismatch'
    $manifest | Set-Content (Join-Path $package 'manifest.json')
    New-Item -ItemType Junction -Path (Join-Path $package 'junction') -Target (Split-Path $data) | Out-Null
    try { Reject { Test-BarkPackage $package } 'reparse point' } finally { [IO.Directory]::Delete((Join-Path $package 'junction')) }
    Assert ((Get-Content $data) -eq 'keep') 'User data touched'
    $count++; Write-Host 'PASS: user data untouched'
    Write-Host "$count isolated update checks passed."
} finally {
    if (Test-Path $registry) { Remove-Item $registry -Recurse -Force }
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
}
