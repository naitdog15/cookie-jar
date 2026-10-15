param([string]$OutDir = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$bin  = if ($OutDir) { $OutDir } else { Join-Path $root 'bin' }
New-Item -ItemType Directory -Force $bin | Out-Null
$out  = Join-Path $bin 'CookieJar.exe'
$wpf  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
$fw   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$files = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$args = @('/nologo','/target:winexe','/platform:x64','/optimize+','/main:RbxMulti.Startup',
  "/out:$out", "/win32manifest:$(Join-Path $root 'app.manifest')",
  "/r:$wpf\PresentationFramework.dll","/r:$wpf\PresentationCore.dll","/r:$fw\WPF\WindowsBase.dll",
  '/r:System.Xaml.dll','/r:System.Management.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.dll','/r:System.Core.dll','/r:Microsoft.CSharp.dll') + $files
$icon = Join-Path $root 'assets\CookieJar.ico'
if (Test-Path $icon) { $args += "/win32icon:$icon" }
& $csc @args
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED"; exit $LASTEXITCODE }
Write-Host "BUILD OK: $out"


