$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $PSScriptRoot 'out/launch fixtures'
New-Item -ItemType Directory -Force $bin | Out-Null
$out = Join-Path $bin 'LaunchTests.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $fw 'WPF'
$files = @(Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName })
$files += Join-Path $PSScriptRoot 'LaunchTests.cs'
$files += Join-Path $PSScriptRoot 'GuardFailureTests.cs'
$files += Join-Path $PSScriptRoot 'LocatorTests.cs'
$files += Join-Path $PSScriptRoot 'LaunchPipelineTests.cs'
$files += Join-Path $PSScriptRoot 'RobloxRepairTests.cs'
$compilerArgs = @('/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/main:RbxMulti.LaunchTests', "/out:$out",
    "/r:$wpf/PresentationFramework.dll", "/r:$wpf/PresentationCore.dll", "/r:$wpf/WindowsBase.dll",
    '/r:System.Xaml.dll', '/r:System.Management.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    '/r:System.dll', '/r:System.Core.dll', '/r:Microsoft.CSharp.dll', '/r:System.Web.Extensions.dll') + $files
& $csc @compilerArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $out
exit $LASTEXITCODE
