param([switch]$Live)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PSScriptRoot 'out/UpdateTests.exe'
New-Item -ItemType Directory -Force (Split-Path -Parent $out) | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:exe /platform:x64 "/out:$out" /r:System.Web.Extensions.dll "/resource:$(Join-Path $root 'scripts/install-update.ps1'),RbxMulti.InstallUpdate.ps1" (Join-Path $root 'src/UpdateService.cs') (Join-Path $root 'src/AppVersion.cs') (Join-Path $PSScriptRoot 'UpdateTests.cs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Live) { & $out --live } else { & $out }
exit $LASTEXITCODE
