param([Parameter(Mandatory = $true)][string]$BackupDirectory)
$ErrorActionPreference = 'Stop'
$runningTrialProcesses = @(Get-Process -Name Attila,CrusaderConflicts -ErrorAction SilentlyContinue)
if ($runningTrialProcesses.Count -gt 0) {
    throw ('Close Attila and Crusader Conflicts before restoring trial files. Running: ' +
        (($runningTrialProcesses | ForEach-Object { $_.ProcessName + ' (PID ' + $_.Id + ')' }) -join ', '))
}
$backupRoot = [IO.Path]::GetFullPath($BackupDirectory)
$manifest = [IO.File]::ReadAllText((Join-Path $backupRoot 'manifest.json')) | ConvertFrom-Json
$gameRoot = [IO.Path]::GetFullPath($manifest.gameRoot)
$allowed = @('data/CrusaderConflicts.pack', 'used_mods_cc.txt', 'data/BattleResults_log.txt', 'steam_appid.txt', 'data/CrusaderWars.png')
$captured = Join-Path $backupRoot ('trial-output-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'))
[IO.Directory]::CreateDirectory($captured) | Out-Null
foreach ($record in $manifest.files) {
    if ($record.relativePath -notin $allowed) { throw 'Unexpected restore target.' }
    $target = [IO.Path]::GetFullPath((Join-Path $gameRoot $record.relativePath))
    if (-not $target.StartsWith($gameRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Restore target escaped game directory.' }
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        $preserve = Join-Path $captured $record.relativePath
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($preserve)) | Out-Null
        Copy-Item -LiteralPath $target -Destination $preserve
    }
    if ($record.existed) {
        $source = Join-Path $backupRoot $record.relativePath
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $record.sha256) { throw 'Original backup digest mismatch.' }
        Copy-Item -LiteralPath $source -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $record.sha256) { throw 'Restored game digest mismatch.' }
    } elseif (Test-Path -LiteralPath $target -PathType Leaf) {
        Remove-Item -LiteralPath $target
    }
}
Write-Output "Original Attila files restored; actual trial outputs preserved at: $captured"
