param()
$ErrorActionPreference='Stop'
if(Get-Process -Name PCFish -ErrorAction SilentlyContinue){throw 'Close PC FISH before removing the mod.'}
$fishTaskProject=Split-Path $PSScriptRoot -Parent
$fishTaskPointer=Join-Path $fishTaskProject 'var\mod-installation.json'
$fishTaskManifest=Get-Content -LiteralPath $fishTaskPointer -Raw | ConvertFrom-Json
$fishTaskGame=(Resolve-Path -LiteralPath $fishTaskManifest.game_root).Path
foreach($fishTaskFile in $fishTaskManifest.files){
    $fishTaskDestination=[IO.Path]::GetFullPath((Join-Path $fishTaskGame $fishTaskFile.relative))
    if(-not $fishTaskDestination.StartsWith($fishTaskGame+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid installed path.'}
    if($fishTaskFile.original_backup){Copy-Item -LiteralPath $fishTaskFile.original_backup -Destination $fishTaskDestination -Force}
    elseif(Test-Path -LiteralPath $fishTaskDestination){Remove-Item -LiteralPath $fishTaskDestination}
}
Move-Item -LiteralPath $fishTaskPointer -Destination ($fishTaskPointer+'.removed-'+(Get-Date -Format yyyyMMddHHmmss))
Write-Output 'Removed installed mod/loader files. Save data and generated diagnostic files are preserved.'
