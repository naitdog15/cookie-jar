param(
    [Parameter(Mandatory=$true)][ValidateSet('Major','Minor','Patch','None')][string]$Bump,
    [ValidateSet('Stable','Alpha','Beta')][string]$Channel = 'Stable',
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$Summary
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-common.ps1')
$current = Read-ReleaseVersion $root
$next = Get-NextReleaseVersion $current $Bump $Channel
$path = Join-Path $root "docs/patch-notes/$next.md"
if (Test-Path -LiteralPath $path) { throw "Notes already exist for $next; do not reuse a release label." }
$date = Get-Date -Format yyyy-MM-dd
$content = @"
# Cookie Jar $next

- Date: $date
- Platform: [FILL IN]

## Briefing

$Summary

## Changes

- [FILL IN]

## Source

- [FILL IN]

## Validation

- [FILL IN]

## Known limitations

- [FILL IN]
"@
New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
[IO.File]::WriteAllText($path, $content + "`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $root 'VERSION'), $next + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Prepared $current -> $next. Complete $path before building or committing."
