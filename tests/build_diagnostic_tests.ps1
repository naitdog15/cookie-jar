$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PSScriptRoot 'out/DiagnosticTests.exe'
New-Item -ItemType Directory -Force (Split-Path -Parent $out) | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /langversion:5 /target:exe /platform:x64 "/out:$out" (Join-Path $root 'src/DiagnosticReport.cs') (Join-Path $PSScriptRoot 'DiagnosticTests.cs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $out
exit $LASTEXITCODE
