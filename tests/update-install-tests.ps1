$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'scripts/install-update.ps1')
$script:passed = 0
function Check([string]$Name, [bool]$Ok) {
    if (!$Ok) { throw "FAIL: $Name" }
    $script:passed++; Write-Host "PASS: $Name"
}
function Reject([string]$Name, [scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { Write-Host "Expected rejection: $($_.Exception.Message)"; $rejected = $true }
    Check $Name $rejected
}
$work = Join-Path $PSScriptRoot ('out/update fixture & test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
$revision = 'a' * 40
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
$zipPath = Join-Path $work 'source.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    $paths = @('build.ps1', 'VERSION', 'app.manifest', 'assets/CookieJar.ico', "docs/patch-notes/$version.md")
    $paths += @(Get-ChildItem (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { 'src/' + $_.Name })
    $paths += @(Get-ChildItem (Join-Path $root 'scripts') -Filter '*.ps1' | ForEach-Object { 'scripts/' + $_.Name })
    foreach ($path in $paths) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root $path), "cookie-jar-$revision/$path") | Out-Null }
} finally { $zip.Dispose() }
$installed = Join-Path $work 'installed'
New-Item -ItemType Directory -Force $installed | Out-Null
$target = Join-Path $installed 'CookieJar.exe'
$notesPath = Join-Path $installed 'PATCH_NOTES.md'
[IO.File]::WriteAllText($target, 'original executable fixture')
[IO.File]::WriteAllText($notesPath, 'original notes fixture')
$config = [pscustomobject]@{ Revision = $revision; Version = $version; WorkDirectory = $work; ArchivePath = $zipPath; TargetPath = $target; MiniBar = $false }
Prepare-SourceUpdate $config (Join-Path $work 'build.log')
Check 'pinned source archive builds without Git checkout' (([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $work 'build/CookieJar.exe'))).ProductVersion -eq $version)
Check 'candidate hash recorded' ($config.BinaryHash.Length -eq 64)
Check 'preparation leaves installed files unchanged' (([IO.File]::ReadAllText($target)) -eq 'original executable fixture')
Install-UpdateFiles $config { param($Target, $MiniBar) }
Check 'install places versioned executable' (([Diagnostics.FileVersionInfo]::GetVersionInfo($target)).ProductVersion -eq $version)
Check 'install keeps previous executable' (([IO.File]::ReadAllText($target + '.previous')) -eq 'original executable fixture')
Check 'install copies source patch notes' ((Get-Content -LiteralPath $notesPath -Raw).Contains('Cookie Jar ' + $version))
[IO.File]::WriteAllText($target, 'rollback original')
[IO.File]::WriteAllText($notesPath, 'rollback notes')
Reject 'restart failure triggers rollback' { Install-UpdateFiles $config { throw 'Simulated restart failure' } }
Check 'rollback restores original executable' (([IO.File]::ReadAllText($target)) -eq 'rollback original')
Check 'rollback restores original notes' (([IO.File]::ReadAllText($notesPath)) -eq 'rollback notes')
$config.BinaryHash = '0' * 64
Reject 'changed candidate rejected before replacement' { Install-UpdateFiles $config { } }
Check 'rejected candidate leaves executable intact' (([IO.File]::ReadAllText($target)) -eq 'rollback original')
$unsafeZip = Join-Path $work 'unsafe.zip'
$zip = [IO.Compression.ZipFile]::Open($unsafeZip, [IO.Compression.ZipArchiveMode]::Create)
try { $zip.CreateEntry('../escaped.txt') | Out-Null } finally { $zip.Dispose() }
Reject 'archive traversal rejected' { Expand-UpdateSource $unsafeZip (Join-Path $work 'unsafe') }
Check 'unsafe archive did not escape staging' (!(Test-Path -LiteralPath (Join-Path $work 'escaped.txt')))
Write-Host "Updater install tests: $script:passed passed. Fixture retained: $work"
