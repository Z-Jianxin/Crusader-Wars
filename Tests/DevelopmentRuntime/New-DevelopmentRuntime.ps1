param(
    [Parameter(Mandatory = $true)][string]$AssetRuntime,
    [Parameter(Mandatory = $true)][string]$Destination,
    [Parameter(Mandatory = $true)][string]$DotNet,
    [string]$AppVersion = '',
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$assetRoot = [IO.Path]::GetFullPath($AssetRuntime)
$stageRoot = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $stageRoot) { throw 'Destination must be new; an existing runtime is never overwritten.' }
foreach ($protectedRoot in @($repoRoot, $assetRoot)) {
    if ($stageRoot.Equals($protectedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $stageRoot.StartsWith($protectedRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Destination must be outside the repository and installed asset runtime.'
    }
}
if (-not (Test-Path -LiteralPath $assetRoot -PathType Container)) { throw 'Asset runtime does not exist.' }
if (-not (Test-Path -LiteralPath $DotNet -PathType Leaf)) { throw 'Pass the .NET 9 SDK dotnet.exe path.' }
foreach ($requiredFile in @('data/runtime/pssuspend64.exe', 'data/rpfm/rpfm_cli.exe', 'data/schema_att.ron')) {
    if (-not (Test-Path -LiteralPath (Join-Path $assetRoot $requiredFile) -PathType Leaf)) {
        throw "Missing required runtime dependency: $requiredFile"
    }
}

[IO.Directory]::CreateDirectory($stageRoot) | Out-Null
$head = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source revision.' }
$changes = @(& git -C $repoRoot status --short)
$marker = [ordered]@{
    purpose = 'Crusader Conflicts stat fidelity development runtime'
    createdUtc = [DateTime]::UtcNow.ToString('o')
    sourceRepository = $repoRoot
    sourceCommit = $head
    sourceHasUncommittedChanges = $changes.Count -gt 0
    assetRuntime = $assetRoot
    automaticUpstreamUpdates = $false
    automaticCk3ModCopy = $false
    originalRuntimeModified = $false
    selfContained = [bool]$SelfContained
    runtimeIdentifier = if ($SelfContained) { 'win-x64' } else { $null }
}
[IO.File]::WriteAllText((Join-Path $stageRoot 'development-runtime.json'), ($marker | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

# Keep the installed battle state, save files, logs and updater executable out.
$folders = @('font', 'Resources', 'settings', 'unit mappers',
    'data/battle files', 'data/traits', 'data/units_cards_names', 'data/terrains_images',
    'data/sounds', 'data/dlls', 'data/mod manager', 'data/rpfm', 'data/runtime')
foreach ($relative in $folders) {
    $source = Join-Path $assetRoot $relative
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Missing runtime asset folder: $relative" }
    $target = Join-Path $stageRoot $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Recurse
}
foreach ($relative in @('app_version.txt', 'um_version.txt', 'data/schema_att.ron')) {
    Copy-Item -LiteralPath (Join-Path $assetRoot $relative) -Destination (Join-Path $stageRoot $relative)
}
if ($AppVersion) {
    if ($AppVersion -notmatch '^v?\d+\.\d+\.\d+(?:[-.][a-zA-Z0-9.-]+)?$') { throw 'AppVersion must be a normal release/development version.' }
    [IO.File]::WriteAllText((Join-Path $stageRoot 'app_version.txt'), ('version="' + $AppVersion + '"'), [Text.UTF8Encoding]::new($false))
}
foreach ($relative in @('data/attila_battle', 'data/save_file_data/temp', 'data/save_file_data/gamestate_file')) {
    [IO.Directory]::CreateDirectory((Join-Path $stageRoot $relative)) | Out-Null
}
$project = Join-Path $repoRoot 'CrusaderWars/CrusaderWars.csproj'
$restoreArguments = @('restore', $project, '--source', 'https://api.nuget.org/v3/index.json', '--verbosity', 'quiet')
if ($SelfContained) { $restoreArguments += @('--runtime', 'win-x64', '-p:SelfContained=true') }
& $DotNet @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
$publishArguments = @('publish', $project, '--no-restore', '--configuration', 'Release', '--output', $stageRoot, '--verbosity', 'quiet', '-p:WarningLevel=0')
if ($SelfContained) { $publishArguments += @('--runtime', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false') }
& $DotNet @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'Development publish failed.' }

$binary = Join-Path $stageRoot 'CrusaderConflicts.dll'
$marker['appVersion'] = [IO.File]::ReadAllText((Join-Path $stageRoot 'app_version.txt'))
$marker['builtAssemblySha256'] = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
$marker['assemblyLastWriteUtc'] = (Get-Item -LiteralPath $binary).LastWriteTimeUtc.ToString('o')
[IO.File]::WriteAllText((Join-Path $stageRoot 'development-runtime.json'), ($marker | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Output "Development runtime prepared (not launched): $stageRoot"
