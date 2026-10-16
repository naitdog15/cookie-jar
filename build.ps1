param([string]$OutDir = '', [switch]$SourceArchive, [string]$SourceRevision = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'scripts\release-common.ps1')
if (!$SourceArchive) { & (Join-Path $root 'scripts\validate-release.ps1') }
elseif ($SourceRevision -notmatch '^[0-9a-f]{40}$') { throw 'Source archive builds require a full source commit SHA.' }
$version = Read-ReleaseVersion $root
$notes = Assert-ReleaseNotes $root $version
$numericVersion = ($version -replace '[ab]$', '') + '.0'
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$bin  = if ($OutDir) { $OutDir } else { Join-Path $root 'bin' }
New-Item -ItemType Directory -Force $bin | Out-Null
$out  = Join-Path $bin 'CookieJar.exe'
$wpf  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
$fw   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$files = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$versionSource = Join-Path $bin 'ReleaseVersion.g.cs'
$metadata = @"
using System.Reflection;
[assembly: AssemblyTitle("Cookie Jar")]
[assembly: AssemblyProduct("Cookie Jar")]
[assembly: AssemblyVersion("$numericVersion")]
[assembly: AssemblyFileVersion("$numericVersion")]
[assembly: AssemblyInformationalVersion("$version")]
"@
[IO.File]::WriteAllText($versionSource, $metadata, [Text.UTF8Encoding]::new($false))
$files += $versionSource
$args = @('/nologo','/target:winexe','/platform:x64','/optimize+','/main:RbxMulti.Startup',
  "/out:$out", "/win32manifest:$(Join-Path $root 'app.manifest')",
  "/r:$wpf\PresentationFramework.dll","/r:$wpf\PresentationCore.dll","/r:$fw\WPF\WindowsBase.dll",
  '/r:System.Xaml.dll','/r:System.Management.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.dll','/r:System.Core.dll','/r:Microsoft.CSharp.dll') + $files
$icon = Join-Path $root 'assets\CookieJar.ico'
$args += '/r:System.Web.Extensions.dll'
$args += "/resource:$(Join-Path $root 'scripts\install-update.ps1'),RbxMulti.InstallUpdate.ps1"
if (Test-Path $icon) { $args += "/win32icon:$icon" }
& $csc @args
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED"; exit $LASTEXITCODE }
if ($SourceArchive) {
  $revision = $SourceRevision
  $branch = 'main (GitHub source archive)'
  $state = 'source archive; built from the requested source snapshot'
} else {
Push-Location $root
try {
  $revision = git rev-parse HEAD
  if ($LASTEXITCODE -ne 0) { throw 'Cannot identify build source revision.' }
  $branch = git branch --show-current
  $changes = @(git status --porcelain --untracked-files=normal)
  $state = if ($changes.Count -eq 0) { 'clean' } else { 'dirty (includes local changes beyond the shown revision)' }
} finally { Pop-Location }
}
$briefing = (Get-Content -LiteralPath $notes -Raw).TrimEnd() + "`n`n## Build source`n`n- Version: $version`n- Branch: $branch`n- Revision: $revision`n- Source state: $state`n"
[IO.File]::WriteAllText((Join-Path $bin 'PATCH_NOTES.md'), $briefing, [Text.UTF8Encoding]::new($false))
Write-Host "BUILD OK: $out (version $version; PATCH_NOTES.md generated)"


