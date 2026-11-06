param(
    [ValidateSet('plan','learn')][string]$Mode = 'plan',
    [string]$InputPath = (Join-Path $PSScriptRoot 'examples\wait-better-pair.json'),
    [string]$OutputPath,
    [string]$PythonPath
)
$ErrorActionPreference = 'Stop'
if (-not $PythonPath) {
    $fishTaskCandidates = @(
        (Join-Path $PSScriptRoot '.venv\Scripts\python.exe'),
        (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe')
    )
    $PythonPath = $fishTaskCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $PythonPath) { throw 'Pass -PythonPath pointing to Python 3.12 or newer.' }
}
$fishTaskPreviousPythonPath = $env:PYTHONPATH
$fishTaskPreviousEncoding = $env:PYTHONIOENCODING
try {
    $env:PYTHONPATH = Join-Path $PSScriptRoot 'src'
    $env:PYTHONIOENCODING = 'utf-8'
    $fishTaskArguments = @('-m', 'pcfish_planner', $Mode, $InputPath)
    if ($OutputPath) { $fishTaskArguments += @('--output', $OutputPath) }
    & $PythonPath @fishTaskArguments
    $fishTaskExitCode = $LASTEXITCODE
} finally {
    $env:PYTHONPATH = $fishTaskPreviousPythonPath
    $env:PYTHONIOENCODING = $fishTaskPreviousEncoding
}
exit $fishTaskExitCode
