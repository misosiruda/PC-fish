param(
    [ValidateSet('Protected','Economic','Observe','Pause','Status')][string]$Mode='Status',
    [string]$GameRoot='C:\Program Files (x86)\Steam\steamapps\common\PC FISH'
)
$ErrorActionPreference='Stop'
$fishTaskConfig=Join-Path $GameRoot 'BepInEx\config\pcfish-automation.json'
$fishTaskRuntime=Join-Path $GameRoot 'BepInEx\PCFishAutomation'
if($Mode -ne 'Status'){
    $fishTaskSettings=Get-Content -LiteralPath $fishTaskConfig -Raw | ConvertFrom-Json
    switch($Mode){
        'Protected' {$fishTaskSettings.enabled=$true;$fishTaskSettings.mode='protected'}
        'Economic' {$fishTaskSettings.enabled=$true;$fishTaskSettings.mode='economic'}
        'Observe' {$fishTaskSettings.enabled=$false;$fishTaskSettings.mode='observe'}
        'Pause' {$fishTaskSettings.enabled=$false}
    }
    $fishTaskTemp=$fishTaskConfig+'.tmp'
    [IO.File]::WriteAllText($fishTaskTemp,($fishTaskSettings | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
    for($fishTaskAttempt=0;;$fishTaskAttempt++){
        try{[IO.File]::Move($fishTaskTemp,$fishTaskConfig,$true);break}
        catch [IO.IOException]{if($fishTaskAttempt -ge 19){throw};Start-Sleep -Milliseconds 5}
        catch [UnauthorizedAccessException]{if($fishTaskAttempt -ge 19){throw};Start-Sleep -Milliseconds 5}
    }
    Write-Output "Automation mode: $Mode (hot reload within 1 second)."
    return
}
foreach($fishTaskName in @('status.json','worker-status.json','recommendation.json')){
    $fishTaskFile=Join-Path $fishTaskRuntime $fishTaskName
    if(Test-Path -LiteralPath $fishTaskFile){Write-Output "$fishTaskName"; Get-Content -LiteralPath $fishTaskFile -Raw}
}
