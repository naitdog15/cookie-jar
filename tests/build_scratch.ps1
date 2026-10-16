param([string]$OutDir = '')
# Builds tests\out\ScratchHost.exe: the real src files + tests\ScratchHost.cs (WPF scratch host with fake instances).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$bin  = if ($OutDir) { $OutDir } else { Join-Path $root 'tests\out' }
New-Item -ItemType Directory -Force $bin | Out-Null
$out  = Join-Path $bin 'ScratchHost.exe'
$wpf  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
$fw   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$files = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$files += (Join-Path $root 'tests\ScratchHost.cs')
$a = @('/nologo','/target:exe','/platform:x64','/optimize+','/main:RbxMulti.ScratchHost',"/out:$out",
  "/r:$wpf\PresentationFramework.dll","/r:$wpf\PresentationCore.dll","/r:$fw\WPF\WindowsBase.dll",
  '/r:System.Xaml.dll','/r:System.Management.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.dll','/r:System.Core.dll','/r:Microsoft.CSharp.dll') + $files
$a += '/r:System.Web.Extensions.dll'
& $csc @a
if ($LASTEXITCODE -ne 0) { Write-Host 'SCRATCH BUILD FAILED'; exit $LASTEXITCODE }
Write-Host "SCRATCH BUILD OK: $out"

