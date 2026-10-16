$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'scripts/release-common.ps1')
$script:passed = 0
function Check([string]$Name, [bool]$Ok) {
    if (!$Ok) { throw "FAIL: $Name" }
    $script:passed++
    Write-Host "PASS: $Name"
}
function Reject([string]$Name, [scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Check $Name $rejected
}
Check 'major resets minor/patch' ((Get-NextReleaseVersion '1.2.3' Major Stable) -eq '2.0.0')
Check 'minor resets patch' ((Get-NextReleaseVersion '1.2.3' Minor Stable) -eq '1.3.0')
Check 'patch increments' ((Get-NextReleaseVersion '1.2.3' Patch Stable) -eq '1.2.4')
Check 'major alpha' ((Get-NextReleaseVersion '1.2.3' Major Alpha) -eq '2.0.0a')
Check 'minor beta' ((Get-NextReleaseVersion '1.2.3' Minor Beta) -eq '1.3.0b')
Check 'patch alpha' ((Get-NextReleaseVersion '1.2.3' Patch Alpha) -eq '1.2.4a')
Check 'alpha promotes to beta' ((Get-NextReleaseVersion '1.2.4a' None Beta) -eq '1.2.4b')
Check 'beta promotes to stable' ((Get-NextReleaseVersion '1.2.4b' None Stable) -eq '1.2.4')
Reject 'same-channel label cannot be reused' { Get-NextReleaseVersion '1.2.4b' None Beta }
Reject 'stable cannot regress to alpha' { Get-NextReleaseVersion '1.2.4' None Alpha }
Reject 'invalid suffix' { Assert-ReleaseVersion '1.2.3rc' }
Reject 'leading zero' { Assert-ReleaseVersion '01.2.3' }
Reject 'assembly version overflow' { Assert-ReleaseVersion '1.2.65535' }
Reject 'version cannot move backwards' { Assert-ReleaseAdvance '2.0.0' '1.9.9' }
Reject 'beta cannot regress to alpha' { Assert-ReleaseAdvance '1.2.3b' '1.2.3a' }
Assert-ReleaseAdvance '1.2.3a' '1.2.3b'
Assert-ReleaseAdvance '1.2.3b' '1.2.3'
Check 'forward promotions accepted' $true
$version = Read-ReleaseVersion $root
$notesPath = Assert-ReleaseNotes $root $version
$notes = Get-Content -LiteralPath $notesPath -Raw
Reject 'unfinished notes rejected' { Assert-ReleaseNotesText ($notes + "`n[FILL IN]") $version }
Reject 'mismatched notes rejected' { Assert-ReleaseNotesText $notes '99.0.0' }
$exe = Join-Path $root 'bin/CookieJar.exe'
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
Check 'exe product version matches source' ($info.ProductVersion -eq $version)
Check 'exe file version is numeric' ($info.FileVersion -eq (($version -replace '[ab]$', '') + '.0'))
$briefing = Get-Content -LiteralPath (Join-Path $root 'bin/PATCH_NOTES.md') -Raw
Check 'distribution includes source notes' ($briefing.StartsWith($notes.TrimEnd()))
Check 'distribution includes source provenance' ($briefing.Contains('## Build source') -and $briefing.Contains('- Revision:') -and $briefing.Contains('- Source state:'))

# Isolated Git fixture: prove staged-content enforcement without touching this repo's index.
$fixture = Join-Path $PSScriptRoot ('out/release-fixture-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force (Join-Path $fixture 'scripts'), (Join-Path $fixture 'src'), (Join-Path $fixture 'docs/patch-notes') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'scripts/release-common.ps1'), (Join-Path $root 'scripts/validate-release.ps1'), (Join-Path $root 'scripts/new-release.ps1') -Destination (Join-Path $fixture 'scripts')
Push-Location $fixture
try {
    git init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize release fixture.' }
    $write = { param($Path, $Text) [IO.File]::WriteAllText((Join-Path $fixture $Path), $Text, [Text.UTF8Encoding]::new($false)) }
    & $write VERSION $version
    & $write "docs/patch-notes/$version.md" $notes
    & $write 'src/example.cs' '// original'
    git add .
    git -c user.name=ReleaseTests -c user.email=release-tests@example.invalid -c core.hooksPath=disabled-hooks commit --quiet -m baseline
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit release fixture baseline.' }
    & $write 'src/example.cs' '// changed'
    git add src/example.cs
    Reject 'staged source without bumped version rejected' { & ./scripts/validate-release.ps1 -Staged }
    $next = Get-NextReleaseVersion $version Patch Beta
    & ./scripts/new-release.ps1 -Bump Patch -Channel Beta -Summary 'Test release briefing'
    Check 'release command writes next version' ((Read-ReleaseVersion $fixture) -eq $next)
    Reject 'generated notes require completion' { Assert-ReleaseNotes $fixture $next }
    $nextNotes = $notes.Replace("# Cookie Jar $version", "# Cookie Jar $next")
    & $write VERSION $next
    & $write "docs/patch-notes/$next.md" $nextNotes
    Reject 'unstaged metadata cannot satisfy staged check' { & ./scripts/validate-release.ps1 -Staged }
    git add VERSION "docs/patch-notes/$next.md"
    & ./scripts/validate-release.ps1 -Staged
    Check 'staged source, version and notes accepted together' $true
    & $write "docs/patch-notes/$next.md" ($nextNotes + "`n[FILL IN]")
    & ./scripts/validate-release.ps1 -Staged
    Check 'staged check reads index rather than working copy' $true
    git add "docs/patch-notes/$next.md"
    Reject 'unfinished staged notes rejected' { & ./scripts/validate-release.ps1 -Staged }
} finally { Pop-Location }
Write-Host "Release tests: $script:passed passed. Fixture kept at $fixture"
