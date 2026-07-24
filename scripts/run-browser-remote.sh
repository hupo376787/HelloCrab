#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
dotnet workload install wasm-tools
dotnet run --project "$ROOT/src/HelloCrab.Browser/HelloCrab.Browser.csproj"
