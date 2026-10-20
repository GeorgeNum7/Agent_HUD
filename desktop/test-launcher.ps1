$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-support.ps1')
function Assert-Hud([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Host ('PASS ' + $Message)
}
$taskDataRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.data'))
$taskFixture = Join-Path $taskDataRoot ('launcher-test-' + [Guid]::NewGuid().ToString('N'))
$taskDesktop = Join-Path $taskFixture 'desktop'
$taskBin = Join-Path $taskDesktop 'bin'
$taskExe = Join-Path $taskBin 'AgentHUD.exe'
New-Item -ItemType Directory -Force -Path $taskDesktop | Out-Null
foreach ($taskName in @('build.ps1', 'build-support.ps1', 'AgentHUD.exe.config', 'app.manifest')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskName) -Destination (Join-Path $taskDesktop $taskName)
}
$taskSource = Join-Path $taskDesktop 'Fixture.cs'
@'
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Security.Cryptography;
class Fixture {
    static void Main(string[] args) {
        var root = Path.GetFullPath(args[0]);
        var hash = BitConverter.ToString(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
        var data = Path.Combine(root, ".data"); Directory.CreateDirectory(data);
        using (var exit = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AgentHUD.Exit." + hash)) {
            File.WriteAllText(Path.Combine(data, "ready"), "ready");
            if (exit.WaitOne(60000)) File.WriteAllText(Path.Combine(data, "graceful-exit"), "saved");
        }
    }
}
'@ | Set-Content -LiteralPath $taskSource -Encoding UTF8
function Start-HudFixture {
    $taskReady = Join-Path $taskFixture '.data\ready'
    if (Test-Path -LiteralPath $taskReady) { Remove-Item -LiteralPath $taskReady }
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList ('"' + $taskFixture + '"') -WindowStyle Hidden -PassThru
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $taskReady) -and [DateTime]::UtcNow -lt $taskDeadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $taskReady)) { throw 'Test fixture did not start.' }
    return $taskProcess
}
try {
    Assert-Hud (Update-HudWidget $taskFixture) 'missing executable is built automatically'
    Assert-Hud (Test-HudBuildCurrent $taskDesktop $taskBin) 'installed binary and config match the source fingerprint'
    Assert-Hud (-not (Update-HudWidget $taskFixture)) 'unchanged source skips compilation'
    $taskBefore = Get-HudSourceHash $taskDesktop
    $taskTime = (Get-Item -LiteralPath $taskSource).LastWriteTimeUtc
    Add-Content -LiteralPath $taskSource -Value '// same-timestamp source change' -Encoding UTF8
    (Get-Item -LiteralPath $taskSource).LastWriteTimeUtc = $taskTime
    Assert-Hud (-not (Test-HudBuildCurrent $taskDesktop $taskBin)) 'content changes are detected even if timestamps are unchanged'
    $taskRunning = Start-HudFixture
    Assert-Hud (Update-HudWidget $taskFixture) 'changed source replaces the running local build'
    Assert-Hud ($taskRunning.WaitForExit(2000) -and (Test-Path -LiteralPath (Join-Path $taskFixture '.data\graceful-exit'))) 'running widget exits through the save-and-close signal'
    Assert-Hud ((Get-HudSourceHash $taskDesktop) -ne $taskBefore -and (Test-HudBuildCurrent $taskDesktop $taskBin)) 'replacement records the new source fingerprint'
    $taskRunning = Start-HudFixture
    $taskWorkingHash = (Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash
    Add-Content -LiteralPath $taskSource -Value 'deliberate compilation failure' -Encoding UTF8
    $taskRejected = $false
    try { $null = Update-HudWidget $taskFixture } catch { $taskRejected = $true }
    Assert-Hud $taskRejected 'invalid source rejects the update'
    Assert-Hud (-not $taskRunning.HasExited) 'failed compilation leaves the working process running'
    Assert-Hud ((Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash -eq $taskWorkingHash) 'failed compilation preserves the working executable'
} finally {
    Stop-HudWidget $taskFixture $taskExe
    $taskResolved = [IO.Path]::GetFullPath($taskFixture)
    if ($taskResolved.StartsWith($taskDataRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $taskResolved)) {
        Remove-Item -LiteralPath $taskResolved -Recurse -Force
    }
}
