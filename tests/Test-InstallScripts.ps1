# Isolated installer smoke test. Mocks only the process/build-hash boundary;
# all copy, backup, update and removal operations run against temporary files.
$ErrorActionPreference='Stop'
$fishTestProject=Split-Path $PSScriptRoot -Parent
$fishTestRoot=Join-Path $fishTestProject ('work\install-test-'+[Guid]::NewGuid().ToString('N'))
$fishTestSource=Join-Path $fishTestRoot 'package'
$fishTestGame=Join-Path $fishTestRoot 'game'
foreach($fishTestFolder in @('package\scripts','package\mod\PCFish.Automation\bin\Release\net6.0','game\BepInEx\core','profile')){
    New-Item -ItemType Directory -Path (Join-Path $fishTestRoot $fishTestFolder) -Force | Out-Null
}
Copy-Item (Join-Path $fishTestProject 'scripts\Install-Mod.ps1'),(Join-Path $fishTestProject 'scripts\Remove-Mod.ps1') (Join-Path $fishTestSource 'scripts')
Set-Content (Join-Path $fishTestSource 'mod\PCFish.Automation\bin\Release\net6.0\PCFish.Automation.dll') 'new-plugin'
Set-Content (Join-Path $fishTestSource 'mod\pcfish-automation.json') '{"enabled":false,"project_root":"example"}'
Set-Content (Join-Path $fishTestGame 'GameAssembly.dll') 'synthetic-game'
Set-Content (Join-Path $fishTestGame 'BepInEx\core\BepInEx.Unity.IL2CPP.dll') 'synthetic-loader'
$fishTestProcess=$false
$fishTestHash='F5F98F82FE2FED5F177B440DF07C9084C814EB8C5C9A141661A67FF184149F93'
function Get-Process { param($Name,$ErrorAction) if($fishTestProcess){[PSCustomObject]@{Name='PCFish'}} }
function Get-FileHash { param($LiteralPath) [PSCustomObject]@{Hash=$fishTestHash} }
function Assert-FishTest { param($Condition,$Message) if(-not $Condition){throw $Message} }
function Assert-FishFailure {
    param([scriptblock]$Action,[string]$Pattern)
    try { & $Action; throw 'Expected rejection did not occur.' }
    catch { if($_.Exception.Message -notlike $Pattern){throw} }
}
$fishTestOldProfile=$env:USERPROFILE
try {
    $env:USERPROFILE=Join-Path $fishTestRoot 'profile'
    $fishTestInstall=Join-Path $fishTestSource 'scripts\Install-Mod.ps1'
    $fishTestRemove=Join-Path $fishTestSource 'scripts\Remove-Mod.ps1'
    $fishTestInstalled=Join-Path $fishTestGame 'BepInEx\plugins\PCFish.Automation\PCFish.Automation.dll'
    $fishTestConfig=Join-Path $fishTestGame 'BepInEx\config\pcfish-automation.json'
    $fishTestProcess=$true
    Assert-FishFailure { & $fishTestInstall -GameRoot $fishTestGame } '*Close PC FISH*'
    $fishTestProcess=$false
    $fishTestHash='unsupported'
    Assert-FishFailure { & $fishTestInstall -GameRoot $fishTestGame } '*not been validated*'
    $fishTestHash='F5F98F82FE2FED5F177B440DF07C9084C814EB8C5C9A141661A67FF184149F93'
    Assert-FishFailure { & $fishTestInstall -LoaderOnly -GameRoot $fishTestGame } '*package is missing*'
    Remove-Item -LiteralPath (Join-Path $fishTestGame 'BepInEx\core\BepInEx.Unity.IL2CPP.dll')
    Assert-FishFailure { & $fishTestInstall -GameRoot $fishTestGame } '*loader first*'
    Set-Content (Join-Path $fishTestGame 'BepInEx\core\BepInEx.Unity.IL2CPP.dll') 'synthetic-loader'
    # Existing game loader is sufficient; no tools/loader source is present.
    & $fishTestInstall -GameRoot $fishTestGame
    Assert-FishTest ((Get-Content $fishTestInstalled -Raw).Trim() -eq 'new-plugin') 'New plugin missing.'
    Assert-FishTest ((Get-Content $fishTestConfig -Raw).Contains('example')) 'Initial config missing.'
    Set-Content $fishTestConfig '{"enabled":false,"project_root":"custom-preserved"}'
    $fishTestConfigBefore=[IO.File]::ReadAllBytes($fishTestConfig)
    Set-Content (Join-Path $fishTestSource 'mod\PCFish.Automation\bin\Release\net6.0\PCFish.Automation.dll') 'updated-plugin'
    & $fishTestInstall -GameRoot $fishTestGame
    Assert-FishTest ((Get-Content $fishTestInstalled -Raw).Trim() -eq 'updated-plugin') 'Updated DLL missing.'
    Assert-FishTest ([Convert]::ToBase64String([IO.File]::ReadAllBytes($fishTestConfig)) -eq [Convert]::ToBase64String($fishTestConfigBefore)) 'Existing config changed.'
    & $fishTestRemove
    Assert-FishTest (-not(Test-Path -LiteralPath $fishTestInstalled)) 'Removal left the installed DLL.'
    Assert-FishTest (Test-Path -LiteralPath (Join-Path $fishTestGame 'BepInEx\core\BepInEx.Unity.IL2CPP.dll')) 'Pre-existing loader was removed.'
    Write-Output 'PASS installer guards, initial install, update, config preservation and scoped removal (isolated fixtures)'
} finally {
    $env:USERPROFILE=$fishTestOldProfile
}
