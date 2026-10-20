function Get-HudIdentity([string]$Root) {
    $taskHasher = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($taskHasher.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($Root).ToLowerInvariant())))).Replace('-', '').Substring(0, 16) }
    finally { $taskHasher.Dispose() }
}

function Get-HudSourceHash([string]$DesktopRoot) {
    $taskInputs = @(Get-ChildItem -LiteralPath $DesktopRoot -Filter '*.cs')
    foreach ($taskName in @('app.manifest', 'AgentHUD.exe.config', 'build.ps1', 'build-support.ps1')) { $taskInputs += Get-Item -LiteralPath (Join-Path $DesktopRoot $taskName) }
    # PS 5.1 (NLS) and PS 7 (ICU) order punctuation differently. Fingerprints must not depend on the host's collation.
    [string[]]$taskNames = @($taskInputs | ForEach-Object { $_.Name })
    [Array]::Sort($taskNames, [StringComparer]::Ordinal)
    $taskLines = @($taskNames | ForEach-Object { $_ + ':' + (Get-FileHash -LiteralPath (Join-Path $DesktopRoot $_) -Algorithm SHA256).Hash })
    $taskHasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($taskHasher.ComputeHash([Text.Encoding]::UTF8.GetBytes(($taskLines -join "`n")))).Replace('-', '') }
    finally { $taskHasher.Dispose() }
}

function Test-HudBuildCurrent([string]$DesktopRoot, [string]$Bin) {
    try {
        $taskStamp = Get-Content -LiteralPath (Join-Path $Bin 'build-state.json') -Raw | ConvertFrom-Json
        return $taskStamp.source -eq (Get-HudSourceHash $DesktopRoot) -and
            $taskStamp.executable -eq (Get-FileHash -LiteralPath (Join-Path $Bin 'AgentHUD.exe') -Algorithm SHA256).Hash -and
            $taskStamp.config -eq (Get-FileHash -LiteralPath (Join-Path $Bin 'AgentHUD.exe.config') -Algorithm SHA256).Hash
    } catch { return $false }
}

function Get-HudWidgetProcesses([string]$Executable) {
    @(Get-Process -Name AgentHUD -ErrorAction SilentlyContinue | Where-Object { $_.Path -and [string]::Equals($_.Path, $Executable, [StringComparison]::OrdinalIgnoreCase) })
}

function Stop-HudWidget([string]$Root, [string]$Executable) {
    $taskInstances = @(Get-HudWidgetProcesses $Executable)
    try {
        $taskExit = [Threading.EventWaitHandle]::OpenExisting('Local\AgentHUD.Exit.' + (Get-HudIdentity $Root))
        try { $null = $taskExit.Set() } finally { $taskExit.Dispose() }
    } catch [Threading.WaitHandleCannotBeOpenedException] { }
    foreach ($taskInstance in $taskInstances) {
        if (-not $taskInstance.WaitForExit(6000)) {
            # Older releases have no graceful exit signal. Only stop this exact project's binary.
            $taskCurrent = Get-Process -Id $taskInstance.Id -ErrorAction SilentlyContinue
            if ($taskCurrent -and [string]::Equals($taskCurrent.Path, $Executable, [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -InputObject $taskCurrent -Force -ErrorAction Stop
                if (-not $taskCurrent.WaitForExit(3000)) { throw 'The previous widget did not exit.' }
            }
        }
    }
}

function Update-HudWidget([string]$Root) {
    $taskDesktopRoot = Join-Path $Root 'desktop'
    $taskBin = Join-Path $taskDesktopRoot 'bin'
    if (Test-HudBuildCurrent $taskDesktopRoot $taskBin) { return $false }
    $taskDataRoot = [IO.Path]::GetFullPath((Join-Path $Root '.data'))
    $taskStaging = Join-Path $taskDataRoot ('widget-build-' + [Guid]::NewGuid().ToString('N'))
    $taskExecutable = [IO.Path]::GetFullPath((Join-Path $taskBin 'AgentHUD.exe'))
    $taskFiles = @('AgentHUD.exe', 'AgentHUD.exe.config', 'build-state.json')
    $taskWasRunning = $false
    $taskReplacing = $false
    try {
        # Compile and validate before interrupting a working widget.
        & (Join-Path $taskDesktopRoot 'build.ps1') -OutputDirectory $taskStaging
        if (-not (Test-HudBuildCurrent $taskDesktopRoot $taskStaging)) { throw 'The source changed during compilation; please launch again.' }
        $taskBackup = Join-Path $taskStaging 'previous'
        New-Item -ItemType Directory -Force -Path $taskBackup | Out-Null
        New-Item -ItemType Directory -Force -Path $taskBin | Out-Null
        foreach ($taskName in $taskFiles) {
            $taskOld = Join-Path $taskBin $taskName
            if (Test-Path -LiteralPath $taskOld) { Copy-Item -LiteralPath $taskOld -Destination (Join-Path $taskBackup $taskName) }
        }
        $taskWasRunning = @(Get-HudWidgetProcesses $taskExecutable).Count -gt 0
        Stop-HudWidget $Root $taskExecutable
        $taskReplacing = $true
        foreach ($taskName in $taskFiles) { Copy-Item -LiteralPath (Join-Path $taskStaging $taskName) -Destination (Join-Path $taskBin $taskName) -Force }
        if (-not (Test-HudBuildCurrent $taskDesktopRoot $taskBin)) { throw 'Installed widget validation failed.' }
        return $true
    } catch {
        if ($taskReplacing) {
            foreach ($taskName in $taskFiles) {
                $taskOld = Join-Path $taskBackup $taskName
                if (Test-Path -LiteralPath $taskOld) { Copy-Item -LiteralPath $taskOld -Destination (Join-Path $taskBin $taskName) -Force }
            }
            if ($taskWasRunning) { Start-Process -FilePath $taskExecutable -ArgumentList ('"' + $Root + '"') -WorkingDirectory $Root }
        }
        throw
    } finally {
        $taskResolvedStage = [IO.Path]::GetFullPath($taskStaging)
        if ($taskResolvedStage.StartsWith($taskDataRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $taskResolvedStage)) {
            Remove-Item -LiteralPath $taskResolvedStage -Recurse -Force
        }
    }
}
