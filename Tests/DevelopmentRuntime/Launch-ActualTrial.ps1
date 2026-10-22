param(
    [Parameter(Mandatory = $true)][string]$TrialDirectory,
    [Parameter(Mandatory = $true)][string]$AttilaDirectory
)
$ErrorActionPreference = 'Stop'
$trial = [IO.Path]::GetFullPath($TrialDirectory)
$game = [IO.Path]::GetFullPath($AttilaDirectory)
$startup = Join-Path $trial 'used_mods_native_snapshot4.txt'
$pack = Join-Path $trial 'cc_fidelity_native_snapshot4.pack'
if (-not (Test-Path -LiteralPath $startup) -or -not (Test-Path -LiteralPath $pack)) { throw 'Prepared trial pack/startup script missing.' }
if (@(Get-Process -Name Attila -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Attila is already running. This script never stops another run.' }
$manifest = [IO.File]::ReadAllText((Join-Path $trial 'pack-manifest.json')) | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $pack -Algorithm SHA256).Hash -ne $manifest.sha256) { throw 'Prepared trial pack digest changed.' }
$log = Join-Path $game 'data/BattleResults_log.txt'
if (Test-Path -LiteralPath $log) {
    Copy-Item -LiteralPath $log -Destination (Join-Path $trial ('prelaunch-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff') + '.log'))
}
[IO.File]::WriteAllText($log, '')
# This launches the interactive game for the explicitly requested native UI test.
$process = Start-Process -FilePath (Join-Path $game 'Attila.exe') -ArgumentList ('"' + $startup + '"') -WorkingDirectory $game -WindowStyle Normal -PassThru
[pscustomobject]@{pid=$process.Id; startup=$startup; packInstalled=$false; actualResultLog=$log; battle='tut_tutorial_battle'; next='Choose the converted Crusader Conflicts battle and begin deployment; automated orders start only after Deployed.'} | ConvertTo-Json
