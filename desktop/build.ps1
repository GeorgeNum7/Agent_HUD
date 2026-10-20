param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-support.ps1')
$taskSourceHash = Get-HudSourceHash $PSScriptRoot
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskCompiler = Join-Path $taskFramework 'csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'Windows .NET Framework C# compiler is required.' }
$taskBin = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot 'bin' }
New-Item -ItemType Directory -Force -Path $taskBin | Out-Null
$taskOutput = Join-Path $taskBin 'AgentHUD.exe'
$taskArgs = @('/nologo', '/target:winexe', '/optimize+', '/utf8output', '/codepage:65001', ('/out:' + $taskOutput), ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')))
foreach ($taskRef in @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) { $taskArgs += '/reference:' + (Join-Path $taskFramework $taskRef) }
foreach ($taskSource in (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Sort-Object Name)) { $taskArgs += $taskSource.FullName }
& $taskCompiler $taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Widget compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AgentHUD.exe.config') -Destination (Join-Path $taskBin 'AgentHUD.exe.config') -Force
if ($taskSourceHash -ne (Get-HudSourceHash $PSScriptRoot)) { throw 'Source files changed during compilation.' }
@{ source = $taskSourceHash; executable = (Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256).Hash; config = (Get-FileHash -LiteralPath ($taskOutput + '.config') -Algorithm SHA256).Hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskBin 'build-state.json') -Encoding UTF8
Write-Host "Built: $taskOutput"
