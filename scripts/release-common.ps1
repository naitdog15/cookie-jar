function Read-ReleaseGitFile([string]$Spec) {
    # Windows PowerShell 5 treats redirected native stderr as an error record.
    # A missing file is expected when bootstrapping versioning or checking the index.
    $ErrorActionPreference = 'Continue'
    $lines = @(git show $Spec 2>$null)
    return @{ Success = ($LASTEXITCODE -eq 0); Lines = $lines }
}

function Read-ReleaseVersion([string]$Root) {
    $version = (Get-Content -LiteralPath (Join-Path $Root 'VERSION') -Raw).Trim()
    return Assert-ReleaseVersion $version
}

function Assert-ReleaseVersion([string]$version) {
    if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)([ab]?)$') {
        throw "Invalid VERSION '$version'. Use X.Y.Z, X.Y.Za or X.Y.Zb."
    }
    $numeric = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
    foreach ($part in $numeric.Split('.')) {
        if ([long]$part -gt 65534) { throw 'Version components must be 0..65534 for Windows assembly metadata.' }
    }
    return $version
}

function Get-NextReleaseVersion([string]$Current, [string]$Bump, [string]$Channel) {
    if ($Current -notmatch '^(\d+)\.(\d+)\.(\d+)([ab]?)$') { throw 'Invalid current version.' }
    $major = [int]$Matches[1]; $minor = [int]$Matches[2]; $patch = [int]$Matches[3]; $oldSuffix = $Matches[4]
    $suffix = switch ($Channel) { 'Alpha' { 'a' }; 'Beta' { 'b' }; 'Stable' { '' }; default { throw 'Unknown release channel.' } }
    switch ($Bump) {
        'Major' { $major++; $minor = 0; $patch = 0 }
        'Minor' { $minor++; $patch = 0 }
        'Patch' { $patch++ }
        'None' {
            if (!(($oldSuffix -eq 'a' -and ($suffix -eq 'b' -or $suffix -eq '')) -or ($oldSuffix -eq 'b' -and $suffix -eq ''))) {
                throw 'Without a numeric bump, only alpha -> beta/stable or beta -> stable promotion is allowed.'
            }
        }
        default { throw 'Unknown version bump.' }
    }
    foreach ($part in @($major, $minor, $patch)) { if ($part -gt 65534) { throw 'Version component exceeds 65534.' } }
    return "$major.$minor.$patch$suffix"
}

function Assert-ReleaseNotes([string]$Root, [string]$Version) {
    $path = Join-Path $Root "docs/patch-notes/$Version.md"
    if (!(Test-Path -LiteralPath $path)) { throw "Missing source patch notes: $path" }
    $notes = Get-Content -LiteralPath $path -Raw
    Assert-ReleaseNotesText $notes $Version
    return $path
}

function Assert-ReleaseNotesText([string]$notes, [string]$Version) {
    if ($notes -notmatch ('(?m)^# Cookie Jar ' + [regex]::Escape($Version) + '\s*$')) { throw 'Patch-notes heading must match VERSION.' }
    foreach ($heading in @('Briefing', 'Changes', 'Source', 'Validation', 'Known limitations')) {
        if ($notes -notmatch ('(?m)^## ' + $heading + '\s*$')) { throw "Patch notes need a '$heading' section." }
    }
    if ($notes -match '\[FILL IN\]') { throw 'Complete all [FILL IN] placeholders in source patch notes before building or committing.' }
}

function Assert-ReleaseAdvance([string]$Previous, [string]$Next) {
    $null = Assert-ReleaseVersion $Previous
    $null = Assert-ReleaseVersion $Next
    $oldNumeric = [version]($Previous -replace '[ab]$', '')
    $newNumeric = [version]($Next -replace '[ab]$', '')
    if ($newNumeric -gt $oldNumeric) { return }
    $ranks = @{ a = 0; b = 1; stable = 2 }
    $oldChannel = if ($Previous -match '([ab])$') { $Matches[1] } else { 'stable' }
    $newChannel = if ($Next -match '([ab])$') { $Matches[1] } else { 'stable' }
    if ($newNumeric -eq $oldNumeric -and $ranks[$newChannel] -gt $ranks[$oldChannel]) { return }
    throw "Version must advance: $Previous -> $Next is not a new release or forward prerelease promotion."
}
