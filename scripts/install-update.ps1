param([string]$Package = (Split-Path -Parent $PSScriptRoot), [switch]$Rollback)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BarkTool.Update.psm1') -Force
try {
    Invoke-BarkDeployment -Package $Package -Rollback:$Rollback
} catch {
    Write-Error $_
    exit 1
}
