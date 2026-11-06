param([string]$GameRoot='C:\Program Files (x86)\Steam\steamapps\common\PC FISH')
$ErrorActionPreference='Stop'
$fishTaskProject=Split-Path $PSScriptRoot -Parent
$fishTaskDotnet=Join-Path $fishTaskProject 'tools\dotnet\dotnet.exe'
if(-not(Test-Path -LiteralPath $fishTaskDotnet)){throw 'Local .NET SDK is missing under tools\dotnet.'}
& $fishTaskDotnet build (Join-Path $fishTaskProject 'mod\PCFish.Automation\PCFish.Automation.csproj') -c Release --nologo "-p:GameRoot=$GameRoot"
if($LASTEXITCODE -ne 0){throw 'Mod build failed.'}
