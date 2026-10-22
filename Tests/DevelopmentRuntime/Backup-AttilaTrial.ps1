param(
    [Parameter(Mandatory = $true)][string]$AttilaDirectory,
    [Parameter(Mandatory = $true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$gameRoot = [IO.Path]::GetFullPath($AttilaDirectory)
$backupRoot = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $backupRoot) { throw 'Backup destination must be new.' }
if (-not (Test-Path -LiteralPath (Join-Path $gameRoot 'Attila.exe'))) { throw 'Attila.exe is missing.' }
if ($backupRoot.StartsWith($gameRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Backup must be outside the game directory.' }
[IO.Directory]::CreateDirectory($backupRoot) | Out-Null
$records = foreach ($relative in @('data/CrusaderConflicts.pack', 'used_mods_cc.txt', 'data/BattleResults_log.txt', 'steam_appid.txt', 'data/CrusaderWars.png')) {
    $source = Join-Path $gameRoot $relative
    $exists = Test-Path -LiteralPath $source -PathType Leaf
    $copy = Join-Path $backupRoot $relative
    if ($exists) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $copy
    }
    [ordered]@{relativePath=$relative; existed=$exists; sha256=if($exists){(Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash}else{$null}}
}
$manifest = [ordered]@{createdUtc=[DateTime]::UtcNow.ToString('o'); gameRoot=$gameRoot; files=@($records); gameFilesChanged=$false}
[IO.File]::WriteAllText((Join-Path $backupRoot 'manifest.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Output "Attila trial backup prepared without changing game files: $backupRoot"
