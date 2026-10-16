param([ValidateSet('Prepare','Install')][string]$Mode, [string]$ConfigPath)
$ErrorActionPreference = 'Stop'

function Expand-UpdateSource([string]$Archive, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $prefix = [IO.Path]::GetFullPath($Destination).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
        $total = 0L
        foreach ($entry in $zip.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $Destination $entry.FullName))
            if (!$target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source archive contains an unsafe path.' }
            $total += $entry.Length
            if ($total -gt 268435456 -or $zip.Entries.Count -gt 10000) { throw 'Source archive exceeds update limits.' }
        }
    } finally { $zip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Destination)
}

function Assert-UpdateBinary($Config) {
    $candidate = Join-Path $Config.WorkDirectory 'build/CookieJar.exe'
    if (!(Test-Path -LiteralPath $candidate)) { throw 'Prepared executable is missing.' }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($candidate).ProductVersion
    if ($version -ne $Config.Version) { throw 'Prepared executable version does not match the requested update.' }
    if ($Config.BinaryHash -and (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $Config.BinaryHash) {
        throw 'Prepared executable changed after verification.'
    }
    $notes = Join-Path $Config.WorkDirectory 'build/PATCH_NOTES.md'
    if (!(Test-Path -LiteralPath $notes)) { throw 'Prepared patch notes are missing.' }
    return $candidate
}

function Prepare-SourceUpdate($Config, [string]$LogPath) {
    if ($Config.Revision -notmatch '^[0-9a-f]{40}$' -or $Config.Version -notmatch '^\d+\.\d+\.\d+[ab]?$') { throw 'Invalid update metadata.' }
    $unpacked = Join-Path $Config.WorkDirectory 'source'
    Expand-UpdateSource $Config.ArchivePath $unpacked
    $folders = @(Get-ChildItem -LiteralPath $unpacked -Directory)
    if ($folders.Count -ne 1) { throw 'Source archive must contain exactly one source root.' }
    $source = $folders[0].FullName
    if (!(Test-Path -LiteralPath (Join-Path $source 'build.ps1'))) { throw 'The Windows source build script is missing.' }
    if ((Get-Content -LiteralPath (Join-Path $source 'VERSION') -Raw).Trim() -ne $Config.Version) { throw 'Source version does not match the checked version.' }
    $psExe = Join-Path $PSHOME 'powershell.exe'
    if (!(Test-Path -LiteralPath $psExe)) { $psExe = (Get-Process -Id $PID).Path }
    $buildOutput = Join-Path $Config.WorkDirectory 'build'
    & $psExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $source 'build.ps1') -OutDir $buildOutput -SourceArchive -SourceRevision $Config.Revision >> $LogPath 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Source build failed (exit $LASTEXITCODE)." }
    $candidate = Assert-UpdateBinary $Config
    $Config | Add-Member -NotePropertyName BinaryHash -NotePropertyValue (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -Force
}

function Install-UpdateFiles($Config, [scriptblock]$Restart) {
    $candidate = Assert-UpdateBinary $Config
    $target = [IO.Path]::GetFullPath($Config.TargetPath)
    if (!(Test-Path -LiteralPath $target) -or [IO.Path]::GetExtension($target) -ne '.exe') { throw 'The installed application could not be found.' }
    $targetDirectory = Split-Path -Parent $target
    $next = Join-Path $targetDirectory ('CookieJar-update-' + [guid]::NewGuid().ToString('N') + '.exe')
    $backup = $target + '.previous'
    $notes = Join-Path $targetDirectory 'PATCH_NOTES.md'
    $oldNotes = if (Test-Path -LiteralPath $notes) { [IO.File]::ReadAllBytes($notes) } else { $null }
    $replaced = $false
    try {
        Copy-Item -LiteralPath $candidate -Destination $next
        [IO.File]::Replace($next, $target, $backup)
        $replaced = $true
        Copy-Item -LiteralPath (Join-Path $Config.WorkDirectory 'build/PATCH_NOTES.md') -Destination $notes -Force
        & $Restart $target ([bool]$Config.MiniBar)
    } catch {
        if ($replaced) {
            Copy-Item -LiteralPath $backup -Destination $target -Force
            if ($null -ne $oldNotes) { [IO.File]::WriteAllBytes($notes, [byte[]]$oldNotes) }
            elseif (Test-Path -LiteralPath $notes) { Remove-Item -LiteralPath $notes }
        }
        throw
    } finally {
        if (Test-Path -LiteralPath $next) { Remove-Item -LiteralPath $next }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $log = $null
    try {
        $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
        $log = Join-Path $config.WorkDirectory 'update.log'
        if ($Mode -eq 'Prepare') {
            Prepare-SourceUpdate $config $log
            [IO.File]::WriteAllText($ConfigPath, ($config | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        } elseif ($Mode -eq 'Install') {
            $null = Assert-UpdateBinary $config
            $parent = Get-Process -Id $config.ParentPid -ErrorAction SilentlyContinue
            [IO.File]::WriteAllText((Join-Path $config.WorkDirectory 'install.ready'), 'ready')
            if ($null -ne $parent -and $parent.StartTime.ToUniversalTime().Ticks.ToString() -eq $config.ParentStartedUtcTicks) {
                if (!$parent.WaitForExit(60000)) { throw 'Cookie Jar did not exit; no files were replaced.' }
            }
            Install-UpdateFiles $config {
                param($Target, $MiniBar)
                $options = @{ FilePath = $Target; WorkingDirectory = (Split-Path -Parent $Target); PassThru = $true }
                if ($MiniBar) { $options.ArgumentList = '--minibar' }
                $app = Start-Process @options
                if ($app.WaitForExit(3000)) { throw 'The updated app exited during startup; the previous executable has been restored.' }
            }
        } else { throw 'Choose Prepare or Install mode.' }
        exit 0
    } catch {
        if ($log) { Add-Content -LiteralPath $log -Value $_.Exception.ToString() }
        if ($Mode -eq 'Install') {
            Add-Type -AssemblyName PresentationFramework
            [Windows.MessageBox]::Show("Cookie Jar could not finish the update. The previous executable is retained or restored. Reopen Cookie Jar manually. Details: $log`n`n$($_.Exception.Message)", 'Cookie Jar update failed') | Out-Null
        }
        exit 1
    }
}
