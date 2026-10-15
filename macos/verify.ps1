# Builds the macos/ projects and runs their tests (plain `dotnet`, SDK 3.1, no NuGet packages, works offline).
#   .\verify.ps1                 everything that exists
#   .\verify.ps1 -Only Core      only the Core library and Core tests (names: Platform, Core, Windows, Mac, AppModel)
# Exit code 0 = every selected project built and every test passed.
param([string[]]$Only = @())
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$map = @(
  @{ n = 'Platform';  lib = 'CookieJar.Platform';         tests = $null },
  @{ n = 'Core';      lib = 'CookieJar.Core';             tests = 'CookieJar.Core.Tests' },
  @{ n = 'Windows';   lib = 'CookieJar.Platform.Windows'; tests = 'CookieJar.Windows.Tests' },
  @{ n = 'Mac';       lib = 'CookieJar.Platform.Mac';     tests = 'CookieJar.Mac.Tests' },
  @{ n = 'AppModel';  lib = 'CookieJar.AppModel';         tests = 'CookieJar.AppModel.Tests' }
)
$failed = @()
foreach ($m in $map) {
  if ($Only.Count -gt 0 -and ($Only -notcontains $m.n)) { continue }
  $csproj = Join-Path $root "src\$($m.lib)\$($m.lib).csproj"
  if (-not (Test-Path $csproj)) { Write-Host "SKIP  $($m.n) (no project)"; continue }
  Write-Host "=== BUILD $($m.n) ==="
  $out = & dotnet build $csproj -nologo -v q -clp:"ErrorsOnly;NoSummary" 2>&1
  if ($LASTEXITCODE -ne 0) { $out | Select-Object -First 40 | ForEach-Object { Write-Host $_ }; Write-Host "BUILD FAILED: $($m.n)"; $failed += "build:$($m.n)"; continue }
  Write-Host "BUILD OK: $($m.n)"
  if ($m.tests) {
    $tproj = Join-Path $root "tests\$($m.tests)\$($m.tests).csproj"
    if (Test-Path $tproj) {
      Write-Host "=== TEST $($m.n) ==="
      $t = & dotnet run --project $tproj -nologo -v q 2>&1
      $t | ForEach-Object { Write-Host $_ }
      if ($LASTEXITCODE -ne 0) { $failed += "tests:$($m.n)" }
    }
  }
}
Write-Host ''
if ($failed.Count -gt 0) { Write-Host ("VERIFY FAILED: " + ($failed -join ', ')); exit 1 }
Write-Host 'VERIFY OK'
exit 0
