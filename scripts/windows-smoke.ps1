param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$exe = (Resolve-Path $Executable).Path
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('LoLKarmaBook-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$failureLog = Join-Path $runRoot 'failure.log'
$arguments = '--data-root "' + $runRoot + '" --smoke 5000 --failure-log "' + $failureLog + '"'
$process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(30000)) {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    throw "Window did not exit within 30 seconds. Evidence: $runRoot"
}
if ($process.ExitCode -ne 0 -or (Test-Path $failureLog)) {
    throw "Application failed. Evidence: $runRoot"
}
$database = Join-Path $runRoot 'data/profiles/default/lolkarmabook.db'
$diagnostics = Join-Path $runRoot 'logs/desktop.log'
if (-not (Test-Path $database) -or (Get-Item $database).Length -eq 0) {
    throw "Default profile database was not created. Evidence: $runRoot"
}
if (-not (Test-Path $diagnostics)) {
    throw "Diagnostic log was not created. Evidence: $runRoot"
}
$log = Get-Content $diagnostics -Raw
if ($log -match '\b(ERROR|WARN)\b' -or $log -notmatch '\[profile\].*Opened profile' -or $log -notmatch '\[smoke\]') {
    throw "Startup/profile/smoke checks failed. Evidence: $runRoot"
}
Write-Host "PASS: startup, default encrypted profile creation, and timed exit. Evidence: $runRoot"
Write-Host 'This is a startup check only. Complete the interactive Windows checklist in docs/desktop.md.'
