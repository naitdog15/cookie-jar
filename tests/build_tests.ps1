param([string]$OutDir = '')
# Builds tests\out\TeleportTests.exe (console, WinForms test window) from the real src files.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$bin  = if ($OutDir) { $OutDir } else { Join-Path $root 'tests\out' }
New-Item -ItemType Directory -Force $bin | Out-Null
$out = Join-Path $bin 'TeleportTests.exe'
$src = Join-Path $root 'src'
$files = @('CashOutDetector.cs','AntiIdleClicker.cs','RobloxInstance.cs','TeleportOcr.cs','TeleportDetector.cs','TeleportService.cs','InputSim.cs','InputDiagnostics.cs','KeepAlive.cs','CashOutManual.cs','ColourBlobs.cs','InviteDetector.cs','HospitalDetector.cs','HospitalRoute.cs','RamScale.cs','BackgroundEfficiency.cs') | ForEach-Object { Join-Path $src $_ }
$files += (Join-Path $root 'tests\TeleportTests.cs')
$files += (Join-Path $root 'tests\InputTests.cs')
$files += (Join-Path $root 'tests\ThemeLoadTests.cs')
$files += (Join-Path $root 'tests\CashOutTests.cs')
$files += (Join-Path $root 'tests\InviteTests.cs')
$files += (Join-Path $root 'tests\HospitalTests.cs')
$files += (Join-Path $root 'tests\WalkTests.cs')
$files += (Join-Path $root 'tests\ClickerOptionTests.cs')
$files += (Join-Path $root 'tests\RequestOrderTests.cs')
$files += (Join-Path $root 'tests\AimSimTests.cs')
$files += (Join-Path $root 'tests\RouteDragTests.cs')
$files += (Join-Path $root 'tests\SwitchTests.cs')
$files += (Join-Path $root 'tests\BackgroundTests.cs')
$a = @('/nologo','/target:exe','/platform:x64','/optimize+','/main:RbxMulti.TeleportTests',"/out:$out",
  '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:Microsoft.CSharp.dll') + $files
& $csc @a
if ($LASTEXITCODE -ne 0) { Write-Host 'TEST BUILD FAILED'; exit $LASTEXITCODE }
Write-Host "TEST BUILD OK: $out"



