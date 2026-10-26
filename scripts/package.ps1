param(
    [string]$Version = '1.1.0.0',
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/package-win-x64')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Version must have four numeric components.' }
$null = [version]$Version
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'BarkTool.Update.psm1') -Force
$outputPath = [IO.Path]::GetFullPath($Output)
Assert-BarkSafeTree $outputPath
if (Test-Path -LiteralPath $outputPath) { throw 'Output already exists; choose a new directory (no destructive clean).' }
New-Item -ItemType Directory -Path $outputPath | Out-Null
& (Join-Path $root '.tools/dotnet/dotnet.exe') publish (Join-Path $root 'src/BarkTool.Desktop/BarkTool.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false "-p:Version=$Version" "-p:FileVersion=$Version" "-p:AssemblyVersion=$Version" -o $outputPath
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
New-Item -ItemType Directory -Path (Join-Path $outputPath 'scripts') | Out-Null
foreach ($name in @('install-update.ps1', 'BarkTool.Update.psm1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $outputPath 'scripts')
}
@{ version = $Version } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath 'version.json') -Encoding UTF8
$files = @(Get-ChildItem -LiteralPath $outputPath -Recurse -File | Sort-Object FullName | ForEach-Object {
    @{ path = $_.FullName.Substring($outputPath.TrimEnd('\','/').Length + 1).Replace('\','/'); sha256 = (Get-BarkHash $_.FullName) }
})
@{ schemaVersion = 1; version = $Version; runtime = 'win-x64'; entryPoint = 'BarkTool.exe'; files = $files } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath 'manifest.json') -Encoding UTF8
$null = Test-BarkPackage $outputPath
Write-Output "Package ready: $outputPath"
