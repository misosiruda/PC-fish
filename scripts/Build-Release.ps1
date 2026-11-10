param([string]$GameRoot='C:\Program Files (x86)\Steam\steamapps\common\PC FISH')
$ErrorActionPreference='Stop'
$fishTaskProject=Split-Path $PSScriptRoot -Parent
$fishTaskPython=Join-Path $fishTaskProject '.venv\Scripts\python.exe'
if(-not(Test-Path -LiteralPath $fishTaskPython)){throw 'Prepare .venv with Python 3.12 or newer first.'}
& $fishTaskPython (Join-Path $PSScriptRoot 'build_release.py') --game-root $GameRoot
if($LASTEXITCODE -ne 0){throw 'Release packaging failed.'}
