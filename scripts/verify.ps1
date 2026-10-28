param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
dotnet build LoLKarmaBook.slnx -c $Configuration -m:1 -nodeReuse:false -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project tests/LoLKarmaBook.Core.Tests -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project tests/LoLKarmaBook.Scoring.Tests -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project tests/LoLKarmaBook.Integration.Tests -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project tests/LoLKarmaBook.Riot.Tests -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project tests/LoLKarmaBook.Desktop.Logic.Tests -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project tests/LoLKarmaBook.Client.Tests -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project tests/LoLKarmaBook.Paths.Tests -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project src/LoLKarmaBook.Cli -c $Configuration --no-build -- verify-performance-reference fixtures/scoring/reference-parity.json
exit $LASTEXITCODE
