#!/usr/bin/env bash
# Builds and runs dotnet/samples/PackageConsumer against the packed packages in ./packages, once per Roslyn version
# given (default 4.8.0) and per target framework (net8.0, net10.0: each consumes its own lib/<tfm>). Run scripts/pack.sh first.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
sample="$root/dotnet/samples/PackageConsumer"
for v in "${@:-4.8.0}"; do
  rm -rf "$sample/obj" "$sample/bin"
  for tfm in net8.0 net10.0; do
    dotnet run --project "$sample/PackageConsumer.csproj" -f "$tfm" -p:RoslynVersion="$v"
  done
done
