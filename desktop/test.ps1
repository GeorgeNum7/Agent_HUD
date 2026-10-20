param([switch]$Baseline)
$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskOutputDir = Join-Path $PSScriptRoot '..\.data\desktop-tests'
New-Item -ItemType Directory -Force -Path $taskOutputDir | Out-Null
$taskOutput = Join-Path $taskOutputDir 'TopmostTests.exe'
$taskArgs = @('/nologo', '/target:exe', '/main:AgentHud.Tests.TopmostTests', '/utf8output', '/codepage:65001', ('/out:' + $taskOutput), ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')))
foreach ($taskRef in @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) { $taskArgs += '/reference:' + (Join-Path $taskFramework $taskRef) }
foreach ($taskSource in (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs')) { $taskArgs += $taskSource.FullName }
foreach ($taskSource in (Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'test') -Filter '*.cs')) { $taskArgs += $taskSource.FullName }
& (Join-Path $taskFramework 'csc.exe') $taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Desktop test compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AgentHUD.exe.config') -Destination ($taskOutput + '.config') -Force
$taskTestArgs = @()
if ($Baseline) { $taskTestArgs += '--baseline' }
& $taskOutput @taskTestArgs
if ($LASTEXITCODE -ne 0) { throw 'Desktop topmost checks failed.' }
