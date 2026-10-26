Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# SHA256 detects corruption, NOT publisher identity or authenticity.
function Get-BarkHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose(); $stream.Dispose() }
}
function Assert-BarkSafeTree([string]$Path, [switch]$AncestorsOnly) {
    $full = [IO.Path]::GetFullPath($Path)
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $cursor" }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }; $cursor = $parent
    }
    if (-not $AncestorsOnly -and (Test-Path -LiteralPath $full)) {
        foreach ($item in Get-ChildItem -LiteralPath $full -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $($item.FullName)" }
            if ($item.PSIsContainer) { Assert-BarkSafeTree $item.FullName }
        }
    }
}

function Test-BarkPackage([string]$Path) {
    Assert-BarkSafeTree $Path
    $manifestPath = Join-Path $Path 'manifest.json'
    $m = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($m.schemaVersion -ne 1 -or $m.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $m.runtime -cne 'win-x64' -or $m.entryPoint -cne 'BarkTool.exe') { throw 'Unsupported manifest/version/entrypoint.' }
    $version = [version]$m.version
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $m.files) {
        $name = [string]$file.path
        if ($name -notmatch '^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$' -or $name -match '(^|/)(\.|\.\.)(/|$)' -or $name -match '(^|/)(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|/|$)' -or $name -match '[. ](/|$)' -or $name -ieq 'manifest.json' -or -not $seen.Add($name)) { throw "Unsafe/duplicate manifest path: $name" }
        $target = Join-Path $Path $name
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Missing payload: $name" }
        if ($file.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-BarkHash $target) -ine $file.sha256) { throw "SHA256 mismatch: $name" }
    }
    if (-not $seen.Contains('BarkTool.exe') -or -not $seen.Contains('BarkTool.dll') -or -not $seen.Contains('version.json')) { throw 'Incomplete application payload.' }
    foreach ($file in Get-ChildItem -LiteralPath $Path -Recurse -File -Force) {
        $name = $file.FullName.Substring([IO.Path]::GetFullPath($Path).TrimEnd('\','/').Length + 1).Replace('\','/')
        if ($name -ine 'manifest.json' -and -not $seen.Contains($name)) { throw "Unlisted payload: $name" }
    }
    $metadata = Get-Content -LiteralPath (Join-Path $Path 'version.json') -Raw | ConvertFrom-Json
    if ($metadata.version -cne $m.version) { throw 'Version metadata mismatch.' }
    $dllVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Path 'BarkTool.dll')).FileVersion
    $exeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Path 'BarkTool.exe')).FileVersion
    if ($dllVersion -ne $m.version -or $exeVersion -ne $m.version) { throw "Binary version mismatch: $dllVersion / $exeVersion" }
    return $m
}

function New-BarkUpdateContext {
    $programs = Join-Path $env:LOCALAPPDATA 'Programs'
    return @{
        Install = Join-Path $programs 'BarkTool'
        Previous = Join-Path $programs 'BarkTool.previous'
        Shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'BarkTool.lnk'
        Registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        StartupApproved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
        BeforeActivate = $null
    }
}

function Assert-BarkStopped([string]$Path) {
    foreach ($process in Get-Process -Name BarkTool -ErrorAction SilentlyContinue) {
        # Conservative refusal also covers inaccessible process paths and portable copies.
        throw 'BarkTool is running. Exit it manually before installing or rolling back.'
    }
    if (-not (Test-Path -LiteralPath $Path)) { return }
    foreach ($file in Get-ChildItem -LiteralPath $Path -File -Recurse -Force) {
        try { $stream = [IO.File]::Open($file.FullName, 'Open', 'ReadWrite', 'None'); $stream.Dispose() }
        catch { throw "Application file is in use or not writable: $($file.FullName). Exit BarkTool manually." }
    }
}

function Invoke-BarkDeployment {
    param([string]$Package, [switch]$Rollback, [hashtable]$Context = (New-BarkUpdateContext))
    $install = [IO.Path]::GetFullPath($Context.Install)
    $previous = [IO.Path]::GetFullPath($Context.Previous)
    if ($install -eq $previous -or [IO.Path]::GetDirectoryName($install) -ne [IO.Path]::GetDirectoryName($previous)) { throw 'Install and previous must be distinct sibling directories.' }
    $parent = [IO.Path]::GetDirectoryName($install)
    Assert-BarkSafeTree $parent -AncestorsOnly
    Assert-BarkSafeTree $install
    Assert-BarkSafeTree $previous
    Assert-BarkSafeTree $Context.Shortcut -AncestorsOnly
    Assert-BarkSafeTree (Join-Path $parent '.BarkTool-update.lock') -AncestorsOnly
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    # Cross-process serialization; unlike a named mutex this also covers different sessions.
    $lock = [IO.File]::Open((Join-Path $parent '.BarkTool-update.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $stage = Join-Path $parent ('.BarkTool-stage-' + [guid]::NewGuid().ToString('N'))
    $savedPrevious = Join-Path $parent ('.BarkTool-backup-' + [guid]::NewGuid().ToString('N'))
    $movedInstall = $false; $activated = $false; $movedPrevious = $false; $success = $false
    $oldShortcut = $null; $hadShortcut = Test-Path -LiteralPath $Context.Shortcut
    $oldRun = $null; $hadRun = $false; $sideEffects = $false
    try {
        Assert-BarkStopped $install
        if ($Rollback) {
            $null = Test-BarkPackage $install
            $null = Test-BarkPackage $previous
            Assert-BarkStopped $previous
            $Package = $previous
        }
        $source = [IO.Path]::GetFullPath($Package).TrimEnd('\','/')
        if ($source -ieq $install -or $source.StartsWith($install + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package must be outside the installation.' }
        $manifest = Test-BarkPackage $source
        New-Item -ItemType Directory -Path $stage | Out-Null
        Copy-Item -Path (Join-Path $source '*') -Destination $stage -Recurse -Force
        $null = Test-BarkPackage $stage
        if ($hadShortcut) { $oldShortcut = [IO.File]::ReadAllBytes($Context.Shortcut) }
        if (Test-Path $Context.Registry) {
            $key = Get-Item $Context.Registry
            $hadRun = $key.GetValueNames() -contains 'BarkTool'
            if ($hadRun) { $oldRun = $key.GetValue('BarkTool', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames); $oldKind = $key.GetValueKind('BarkTool') }
        }
        if (Test-Path -LiteralPath $previous) { Move-Item -LiteralPath $previous -Destination $savedPrevious; $movedPrevious = $true }
        if (Test-Path -LiteralPath $install) { Move-Item -LiteralPath $install -Destination $previous; $movedInstall = $true }
        if ($Context.BeforeActivate) { & $Context.BeforeActivate }
        Move-Item -LiteralPath $stage -Destination $install; $activated = $true
        $sideEffects = $true
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Context.Shortcut)) -Force | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($Context.Shortcut)
        $shortcut.TargetPath = Join-Path $install 'BarkTool.exe'
        $shortcut.WorkingDirectory = $install
        $shortcut.Save()
        # Presence of a nonempty existing Run value is the opt-in. Never create a new opt-in.
        $startupEnabled = $true
        if ($Context.StartupApproved -and (Test-Path $Context.StartupApproved)) {
            $approval = (Get-Item $Context.StartupApproved).GetValue('BarkTool')
            if ($null -ne $approval) { $startupEnabled = ($approval -is [byte[]] -and $approval.Length -ge 1 -and $approval[0] -eq 2) }
        }
        if ($hadRun -and $startupEnabled -and -not [string]::IsNullOrWhiteSpace([string]$oldRun)) {
            Set-ItemProperty -Path $Context.Registry -Name BarkTool -Value ('"' + (Join-Path $install 'BarkTool.exe') + '"') -Type String
        }
        $null = Test-BarkPackage $install
        $success = $true
        Write-Output "Installed BarkTool $($manifest.version) at $install. SHA256 integrity checked; publisher authenticity is not verified."
    }
    catch {
        $failure = $_
        # Restore payload first: a shortcut/registry restoration failure must not strand the app.
        if ($activated) { Remove-Item -LiteralPath $install -Recurse -Force }
        if ($movedInstall) { Move-Item -LiteralPath $previous -Destination $install }
        if ($movedPrevious) { Move-Item -LiteralPath $savedPrevious -Destination $previous }
        if ($sideEffects) {
            if ($hadShortcut) { [IO.File]::WriteAllBytes($Context.Shortcut, $oldShortcut) }
            elseif (Test-Path -LiteralPath $Context.Shortcut) { Remove-Item -LiteralPath $Context.Shortcut -Force }
            if ($hadRun) { Set-ItemProperty -Path $Context.Registry -Name BarkTool -Value $oldRun -Type $oldKind }
        }
        throw $failure
    }
    finally {
        $cleanup = @($stage)
        if ($success) { $cleanup += $savedPrevious }
        foreach ($temporary in $cleanup) {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction Continue }
        }
        $lock.Dispose()
    }
}

Export-ModuleMember -Function Get-BarkHash, Test-BarkPackage, New-BarkUpdateContext, Invoke-BarkDeployment, Assert-BarkSafeTree
