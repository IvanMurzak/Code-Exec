#!/usr/bin/env bash
# Everything CI runs, locally: .NET tests (default Roslyn), pack + package consumer, Python tests.
# Roslyn matrix: ROSLYN_VERSIONS="3.8.0 4.8.0 5.9.0" scripts/test-all.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
for v in ${ROSLYN_VERSIONS:-4.8.0}; do
  echo "== .NET tests, Roslyn $v"
  dotnet test "$root/dotnet/CodeExec.Tests/CodeExec.Tests.csproj" -c Release -p:RoslynVersion="$v"
done
echo "== pack + package consumer"
"$root/scripts/pack.sh" "$root/packages"
rm -rf "$root/dotnet/samples/PackageConsumer/obj" "$root/dotnet/samples/PackageConsumer/bin"
dotnet run --project "$root/dotnet/samples/PackageConsumer/PackageConsumer.csproj"
echo "== Python tests"
if command -v python3 >/dev/null; then python=(python3)
elif command -v python >/dev/null; then python=(python)
elif command -v py >/dev/null; then python=(py -3)   # Windows launcher
else echo "test-all: no Python 3 on PATH" >&2; exit 1
fi
"${python[@]}" -B -m unittest discover -s "$root/python/tests" -v
