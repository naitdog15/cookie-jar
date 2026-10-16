$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$outDir = Join-Path $root 'tests\out'
New-Item -ItemType Directory -Force $outDir | Out-Null
$out = Join-Path $outDir 'HardwareLimitTests.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:exe /platform:x64 /optimize+ "/out:$out" /r:System.Management.dll (Join-Path $root 'src\HardwareProfile.cs') (Join-Path $root 'src\Restrictor.cs') (Join-Path $root 'src\AutoRamPolicy.cs') (Join-Path $root 'src\RamScale.cs') (Join-Path $root 'tests\HardwareLimitTests.cs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $out
exit $LASTEXITCODE
