# Internal pipe-only helper. No credentials are passed on the command line or written to disk.
$ErrorActionPreference = 'Stop'
try {
  Add-Type -AssemblyName System.Security
  $taskEncrypted = [Convert]::FromBase64String([Console]::In.ReadToEnd().Trim())
  $taskKey = [Security.Cryptography.ProtectedData]::Unprotect($taskEncrypted, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
  [Console]::Out.Write([Convert]::ToBase64String($taskKey))
  [Array]::Clear($taskKey, 0, $taskKey.Length)
} catch { [Console]::Error.Write('Claude local credential is unavailable.'); exit 1 }
