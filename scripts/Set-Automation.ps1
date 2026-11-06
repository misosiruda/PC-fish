param(
    [ValidateSet('Protected','Economic','Observe','Pause','Status')][string]$Mode='Status',
    [ValidateSet('Grade','Market')][string]$Protection,
    [ValidateRange(1,100000000)][double]$MarketMinimumKrw=1000,
    [string]$GameRoot='C:\Program Files (x86)\Steam\steamapps\common\PC FISH'
)
$ErrorActionPreference='Stop'
$fishTaskConfig=Join-Path $GameRoot 'BepInEx\config\pcfish-automation.json'
$fishTaskRuntime=Join-Path $GameRoot 'BepInEx\PCFishAutomation'
if($Mode -ne 'Status' -or $Protection){
    if($Protection -eq 'Market'){
        $fishTaskStatusPath=Join-Path $fishTaskRuntime 'status.json'
        if(Test-Path -LiteralPath $fishTaskStatusPath){
            $fishTaskLiveStatus=Get-Content -LiteralPath $fishTaskStatusPath -Raw | ConvertFrom-Json
            $fishTaskNow=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()/1000.0
            if((Get-Process -Name PCFish -ErrorAction SilentlyContinue) -and $fishTaskNow-$fishTaskLiveStatus.captured_at -lt 10 -and [version]$fishTaskLiveStatus.version -lt [version]'0.3.0'){
                throw 'Market protection requires mod 0.3.0 or newer. Close the game, install the new plugin, and restart before switching.'
            }
        }
    }
    $fishTaskSettings=Get-Content -LiteralPath $fishTaskConfig -Raw | ConvertFrom-Json
    switch($Mode){
        'Protected' {$fishTaskSettings.enabled=$true;$fishTaskSettings.mode='protected'}
        'Economic' {$fishTaskSettings.enabled=$true;$fishTaskSettings.mode='economic'}
        'Observe' {$fishTaskSettings.enabled=$false;$fishTaskSettings.mode='observe'}
        'Pause' {$fishTaskSettings.enabled=$false}
    }
    if($Protection){
        $fishTaskSettings | Add-Member -NotePropertyName protection_mode -NotePropertyValue $Protection.ToLowerInvariant() -Force
        if($Protection -eq 'Market'){
            $fishTaskSettings | Add-Member -NotePropertyName market_min_seller_proceeds_krw -NotePropertyValue $MarketMinimumKrw -Force
            $fishTaskSettings | Add-Member -NotePropertyName market_quote_ttl_seconds -NotePropertyValue 86400 -Force
            $fishTaskSettings | Add-Member -NotePropertyName market_request_interval_seconds -NotePropertyValue 30 -Force
        }
    }
    $fishTaskTemp=$fishTaskConfig+'.tmp'
    [IO.File]::WriteAllText($fishTaskTemp,($fishTaskSettings | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
    for($fishTaskAttempt=0;;$fishTaskAttempt++){
        try{[IO.File]::Move($fishTaskTemp,$fishTaskConfig,$true);break}
        catch [IO.IOException]{if($fishTaskAttempt -ge 19){throw};Start-Sleep -Milliseconds 5}
        catch [UnauthorizedAccessException]{if($fishTaskAttempt -ge 19){throw};Start-Sleep -Milliseconds 5}
    }
    Write-Output "Automation mode: $Mode; protection: $($fishTaskSettings.protection_mode) (hot reload within 1 second)."
    return
}
foreach($fishTaskName in @('status.json','worker-status.json','recommendation.json','market-review.json')){
    $fishTaskFile=Join-Path $fishTaskRuntime $fishTaskName
    if(Test-Path -LiteralPath $fishTaskFile){Write-Output "$fishTaskName"; Get-Content -LiteralPath $fishTaskFile -Raw}
}
