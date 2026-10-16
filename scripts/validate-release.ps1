param([switch]$Staged)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-common.ps1')
Push-Location $root
try {
    $previousFile = Read-ReleaseGitFile 'HEAD:VERSION'
    $previousLines = $previousFile.Lines
    $hasPrevious = $previousFile.Success
    if ($Staged) {
        $versionFile = Read-ReleaseGitFile ':VERSION'
        if (!$versionFile.Success) { throw 'Stage VERSION before committing.' }
        $version = Assert-ReleaseVersion (($versionFile.Lines -join "`n").Trim())
        $notesFile = Read-ReleaseGitFile ":docs/patch-notes/$version.md"
        if (!$notesFile.Success) { throw "Stage docs/patch-notes/$version.md before committing." }
        Assert-ReleaseNotesText ($notesFile.Lines -join "`n") $version
        $changed = @(git diff --cached --name-only --diff-filter=ACDMRT)
    } else {
        $version = Read-ReleaseVersion $root
        $null = Assert-ReleaseNotes $root $version
        $changed = @(git diff HEAD --name-only --diff-filter=ACDMRT)
        $changed += @(git ls-files --others --exclude-standard)
    }
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect release changes.' }
    $appChanges = @($changed | Where-Object { $_ -match '^(src/|macos/src/|scripts/|build\.ps1$|Directory\.Build\.(props|targets)$|\.githooks/)' })
    if ($Staged -and $appChanges.Count -gt 0) {
        if ('VERSION' -notin $changed -or "docs/patch-notes/$version.md" -notin $changed) {
            throw 'Stage the version change and matching patch notes in the same commit as the application/build changes.'
        }
    }
    if ($hasPrevious) {
        $previous = ($previousLines -join "`n").Trim()
        if ($version -ne $previous) {
            Assert-ReleaseAdvance $previous $version
        } elseif ($appChanges.Count -gt 0) {
            throw 'Application/build tooling changed without a version bump. Run scripts/new-release.ps1 and complete its source patch notes.'
        }
    }
    Write-Host "RELEASE CHECK OK: $version"
} finally { Pop-Location }
