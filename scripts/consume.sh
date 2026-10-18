#!/usr/bin/env bash
# Builds and runs dotnet/samples/PackageConsumer against the packed packages in ./packages, once per Roslyn version
# given (default 4.8.0). Run scripts/pack.sh first.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
sample="$root/dotnet/samples/PackageConsumer"
for v in "${@:-4.8.0}"; do
  rm -rf "$sample/obj" "$sample/bin"
  dotnet run --project "$sample/PackageConsumer.csproj" -p:RoslynVersion="$v"
done
