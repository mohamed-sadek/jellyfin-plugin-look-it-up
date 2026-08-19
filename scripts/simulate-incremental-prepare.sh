#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/tools/LookItUp.IncrementalPrepareSimulator/LookItUp.IncrementalPrepareSimulator.csproj"

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" || "${#}" -eq 0 ]]; then
  dotnet run --project "$PROJECT" -- --help
  exit 0
fi

dotnet run --project "$PROJECT" --no-launch-profile -- "$@"
