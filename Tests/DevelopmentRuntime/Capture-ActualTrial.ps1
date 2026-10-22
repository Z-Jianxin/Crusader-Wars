param(
    [Parameter(Mandatory = $true)][string]$TrialDirectory,
    [Parameter(Mandatory = $true)][string]$RuntimeDirectory,
    [Parameter(Mandatory = $true)][string]$AttilaDirectory,
    [string]$InputEvidenceDirectory = ''
)
$ErrorActionPreference = 'Stop'
$trial = [IO.Path]::GetFullPath($TrialDirectory)
$runtime = [IO.Path]::GetFullPath($RuntimeDirectory)
$game = [IO.Path]::GetFullPath($AttilaDirectory)
$log = Join-Path $game 'data/BattleResults_log.txt'
$raw = [IO.File]::ReadAllText($log)
$snapshots = @([regex]::Matches($raw, '(?m)^CC_ACTUAL_TRIAL\|SNAPSHOT\|(snapshot-\d+)\r?$') | ForEach-Object {$_.Groups[1].Value} | Select-Object -Unique)
if ($snapshots.Count -ne 1) { throw 'Need exactly one actual trial snapshot marker, not a mixed or unbound result log.' }
$victory = [regex]::IsMatch($raw, '(?m)^Victory\r?$')
$defeat = [regex]::IsMatch($raw, '(?m)^Defeat\r?$')
if ($victory -eq $defeat -or -not $raw.Contains('-----PRINT ENDED-----!!') -or -not $raw.Contains('Battle has finished')) {
    throw 'The real battle has not produced one consistent complete outcome and dismissal marker.'
}
$destination = Join-Path $trial ('evidence-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'))
[IO.Directory]::CreateDirectory($destination) | Out-Null
Copy-Item -LiteralPath $log -Destination (Join-Path $destination 'BattleResults_log.txt')
foreach ($relative in @('data/debug.log','data/battle.log','data/stat_fidelity_coverage.json','data/save_file_data/last_save.zip','data/save_file_data/metadata.txt','data/attila_battle/battle_log_snippet.txt','data/attila_battle/battle_log_snippet.txt.bak','development-runtime.json','settings/Options.xml','settings/StatFidelity.json','settings/UnitMappers.xml','settings/ActiveSubmods.xml')) {
    $source = Join-Path $runtime $relative
    if (Test-Path -LiteralPath $source -PathType Leaf) {
        $target = Join-Path $destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }
}
foreach ($relative in @('data/battle files/script','data/battle files/db')) {
    $source = Join-Path $runtime $relative
    if (Test-Path -LiteralPath $source -PathType Container) {
        $target = Join-Path $destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Recurse
    }
}
Copy-Item -LiteralPath (Join-Path $game 'used_mods_cc.txt') -Destination (Join-Path $destination 'actual-used-mods.txt')
$installedPack = Join-Path $game 'data/CrusaderConflicts.pack'
$capturedPack = Join-Path $destination 'CrusaderConflicts.pack'
Copy-Item -LiteralPath $installedPack -Destination $capturedPack
$packHash = (Get-FileHash -LiteralPath $capturedPack -Algorithm SHA256).Hash
$packedSource = Join-Path $destination 'launched-pack'
[IO.Directory]::CreateDirectory($packedSource) | Out-Null
# The app clears loose declarations/start Lua after packing. Extract the actual
# launched artifact as the authoritative deployed-unit and script inventory.
& (Join-Path $runtime 'data/rpfm/rpfm_cli.exe') --game attila pack extract -p $capturedPack -t (Join-Path $runtime 'data/schema_att.ron') -F ('script;' + $packedSource) -F ('db;' + $packedSource) *> (Join-Path $destination 'pack-extract.log')
if ($LASTEXITCODE -ne 0) { throw 'Captured pack extraction failed; retained original pack and raw evidence for inspection.' }
$result = Get-ChildItem -LiteralPath (Join-Path $runtime 'results') -Filter 'result-manifest.json' -Recurse | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $result) { throw "Real log was captured at $destination, but the app has not published its separate result save." }
$record = [IO.File]::ReadAllText($result.FullName) | ConvertFrom-Json
if ($record.snapshot -ne $snapshots[0]) { throw 'Published app result belongs to a different native snapshot.' }
Copy-Item -LiteralPath $result.Directory.FullName -Destination (Join-Path $destination 'app-result') -Recurse
# Continue Battle starts the next listener after successful import and can clear
# its working input. Accept an explicit immutable native-input archive, but verify
# its copied bytes against the actual app result manifest before declaring success.
if ($InputEvidenceDirectory) {
    $inputEvidence = [IO.Path]::GetFullPath($InputEvidenceDirectory)
    foreach ($relative in @('data/save_file_data/last_save.zip','data/save_file_data/metadata.txt','data/attila_battle/battle_log_snippet.txt')) {
        $source = Join-Path $inputEvidence $relative
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            $target = Join-Path $destination $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
        elseif ($relative -ne 'data/save_file_data/metadata.txt') { throw "Missing archived native input: $relative" }
    }
}
elseif (-not (Test-Path -LiteralPath (Join-Path $destination 'data/attila_battle/battle_log_snippet.txt'))) {
    $snippetBackup = Join-Path $destination 'data/attila_battle/battle_log_snippet.txt.bak'
    if (Test-Path -LiteralPath $snippetBackup) {
        Copy-Item -LiteralPath $snippetBackup -Destination (Join-Path $destination 'data/attila_battle/battle_log_snippet.txt')
    }
}
$capturedInput = Join-Path $destination 'data/save_file_data/last_save.zip'
$capturedOutput = Join-Path $destination 'app-result/battle_results.ck3'
$inputHash = (Get-FileHash -LiteralPath $capturedInput -Algorithm SHA256).Hash
$outputHash = (Get-FileHash -LiteralPath $capturedOutput -Algorithm SHA256).Hash
$aarHash = (Get-FileHash -LiteralPath (Join-Path $destination 'BattleResults_log.txt') -Algorithm SHA256).Hash
if ($inputHash -ne $record.inputSaveSha256 -or $outputHash -ne $record.outputSaveSha256) {
    throw 'Captured input/output bytes do not match the app result manifest; retained evidence without declaring a successful capture.'
}
if ($record.attilaResultSha256 -and $aarHash -ne $record.attilaResultSha256) {
    throw 'Captured native AAR differs from the immutable result bundle; retained both for inspection instead of claiming a bound capture.'
}
$capturedFrame = [IO.File]::ReadAllText((Join-Path $destination 'data/attila_battle/battle_log_snippet.txt'))
if ($record.handoffSha256 -and
    (Get-FileHash -LiteralPath (Join-Path $destination 'data/attila_battle/battle_log_snippet.txt') -Algorithm SHA256).Hash -ne $record.handoffSha256) {
    throw 'Captured handoff differs from the immutable result bundle.'
}
$frameSnapshots = @([regex]::Matches($capturedFrame, '(?m)^CCSTAT\|v1\|END\|(snapshot-\d+)\r?$') | ForEach-Object {$_.Groups[1].Value})
if ($frameSnapshots.Count -ne 1 -or $frameSnapshots[0] -ne $snapshots[0] -or
    -not $capturedFrame.Contains('CCHANDOFF|v1|BEGIN') -or -not $capturedFrame.Contains('CCHANDOFF|v1|END')) {
    throw 'Captured handoff does not contain one complete frame for this actual result snapshot.'
}
[pscustomobject]@{capturedUtc=[DateTime]::UtcNow.ToString('o'); snapshot=$snapshots[0]; nativeWinner=if($victory){'player'}else{'enemy'}; actualResults=$true; syntheticResults=$false; ck3ReloadVerified=$false; launchedPackSha256=$packHash; authoritativeBattleFiles='launched-pack'; inputEvidenceDirectory=$InputEvidenceDirectory; logSha256=(Get-FileHash -LiteralPath (Join-Path $destination 'BattleResults_log.txt') -Algorithm SHA256).Hash; outputSaveSha256=$outputHash; inputSaveSha256=$inputHash; capturedSaveHashesVerified=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'evidence.json') -Encoding UTF8
Write-Output "Actual log and app-produced separate save captured: $destination"
