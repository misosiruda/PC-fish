param([switch]$LoaderOnly, [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\PC FISH')
$ErrorActionPreference = 'Stop'
$fishTaskProject = Split-Path $PSScriptRoot -Parent
$fishTaskLoader = Join-Path $fishTaskProject 'tools\BepInEx-IL2CPP-be793'
$fishTaskPlugin = Join-Path $fishTaskProject 'mod\PCFish.Automation\bin\Release\net6.0\PCFish.Automation.dll'
$fishTaskVar = Join-Path $fishTaskProject 'var'
New-Item -ItemType Directory -Path $fishTaskVar -Force | Out-Null
$fishTaskPointer = Join-Path $fishTaskVar 'mod-installation.json'
if (Get-Process -Name PCFish -ErrorAction SilentlyContinue) { throw 'Close PC FISH before installing or updating the mod.' }
$fishTaskGame = (Resolve-Path -LiteralPath $GameRoot).Path
$fishTaskHash = (Get-FileHash -LiteralPath (Join-Path $fishTaskGame 'GameAssembly.dll')).Hash
if ($fishTaskHash -ne 'F5F98F82FE2FED5F177B440DF07C9084C814EB8C5C9A141661A67FF184149F93') { throw 'This game build has not been validated. Reinspect bindings before installation.' }
if ($LoaderOnly -and -not (Test-Path -LiteralPath $fishTaskLoader)) { throw 'BepInEx be.793 package is missing under tools.' }
if (-not $LoaderOnly -and -not (Test-Path -LiteralPath (Join-Path $fishTaskGame 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'))) { throw 'Install the BepInEx IL2CPP loader first.' }
if (-not $LoaderOnly -and -not (Test-Path -LiteralPath $fishTaskPlugin)) { throw 'Build the plugin before installation, or use -LoaderOnly.' }
if (Test-Path -LiteralPath $fishTaskPointer) {
    $fishTaskManifest = Get-Content -LiteralPath $fishTaskPointer -Raw | ConvertFrom-Json
    if ($fishTaskManifest.game_root -ne $fishTaskGame) { throw 'Existing installation points to a different game folder.' }
} else {
    $fishTaskBackup = Join-Path $fishTaskProject ('work\mod-20261106\install-' + (Get-Date -Format yyyyMMdd-HHmmss))
    New-Item -ItemType Directory -Path $fishTaskBackup -Force | Out-Null
    $fishTaskSaves = Join-Path $env:USERPROFILE 'AppData\LocalLow\NANOO COMPANY Inc_\PCFish'
    foreach($fishTaskName in @('SaveFile.es3','Setting.es3')) {
        $fishTaskSave = Join-Path $fishTaskSaves $fishTaskName
        if(Test-Path -LiteralPath $fishTaskSave){Copy-Item -LiteralPath $fishTaskSave -Destination (Join-Path $fishTaskBackup $fishTaskName)}
    }
    $fishTaskManifest = [PSCustomObject]@{game_root=$fishTaskGame;game_assembly_sha256=$fishTaskHash;backup_root=$fishTaskBackup;loader='6.0.0-be.793+dbc82f0';files=@();installed_utc=[DateTime]::UtcNow.ToString('o')}
}
$fishTaskFiles = @()
if ($LoaderOnly) {
    $fishTaskFiles = @(Get-ChildItem -LiteralPath $fishTaskLoader -Recurse -File | ForEach-Object { [PSCustomObject]@{source=$_.FullName;relative=[IO.Path]::GetRelativePath($fishTaskLoader,$_.FullName)} })
} else {
    $fishTaskFiles = @([PSCustomObject]@{source=$fishTaskPlugin;relative='BepInEx\plugins\PCFish.Automation\PCFish.Automation.dll'})
    if(-not(Test-Path -LiteralPath (Join-Path $fishTaskGame 'BepInEx\config\pcfish-automation.json'))){
        $fishTaskFiles += [PSCustomObject]@{source=(Join-Path $fishTaskProject 'mod\pcfish-automation.json');relative='BepInEx\config\pcfish-automation.json'}
    }
}
foreach($fishTaskFile in $fishTaskFiles){
    $fishTaskDestination = [IO.Path]::GetFullPath((Join-Path $fishTaskGame $fishTaskFile.relative))
    if(-not $fishTaskDestination.StartsWith($fishTaskGame + '\',[StringComparison]::OrdinalIgnoreCase)){throw 'Destination escaped game root.'}
    if(-not ($fishTaskManifest.files | Where-Object {$_.relative -eq $fishTaskFile.relative})){
        $fishTaskOriginal = $null
        if(Test-Path -LiteralPath $fishTaskDestination){
            $fishTaskOriginal = Join-Path $fishTaskManifest.backup_root ('originals\'+$fishTaskFile.relative)
            New-Item -ItemType Directory -Path (Split-Path $fishTaskOriginal -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $fishTaskDestination -Destination $fishTaskOriginal
        }
        $fishTaskManifest.files = @($fishTaskManifest.files) + @([PSCustomObject]@{relative=$fishTaskFile.relative;original_backup=$fishTaskOriginal})
        $fishTaskManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fishTaskPointer
    }
    New-Item -ItemType Directory -Path (Split-Path $fishTaskDestination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $fishTaskFile.source -Destination $fishTaskDestination -Force
}
$fishTaskManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fishTaskPointer
Write-Output "Installed $($fishTaskFiles.Count) files. Manifest: $fishTaskPointer"
