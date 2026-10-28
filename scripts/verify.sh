#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_root"
dotnet_path="${DOTNET_COMMAND:-dotnet}"
if [[ -x "$project_root/.tools/dotnet/dotnet" ]]; then
  dotnet_path="$project_root/.tools/dotnet/dotnet"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
"$dotnet_path" build LoLKarmaBook.slnx -c Release
"$dotnet_path" run --project tests/LoLKarmaBook.Core.Tests -c Release --no-build
"$dotnet_path" run --project tests/LoLKarmaBook.Scoring.Tests -c Release --no-build
"$dotnet_path" run --project tests/LoLKarmaBook.Integration.Tests -c Release --no-build
"$dotnet_path" run --project tests/LoLKarmaBook.Riot.Tests -c Release --no-build
"$dotnet_path" run --project tests/LoLKarmaBook.Desktop.Logic.Tests -c Release --no-build
"$dotnet_path" run --project tests/LoLKarmaBook.Client.Tests -c Release --no-build
"$dotnet_path" run --project tests/LoLKarmaBook.Paths.Tests -c Release --no-build
"$dotnet_path" run --project src/LoLKarmaBook.Cli -c Release --no-build -- verify-performance-reference fixtures/scoring/reference-parity.json
