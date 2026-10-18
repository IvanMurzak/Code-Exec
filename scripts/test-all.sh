#!/usr/bin/env bash
# Everything CI runs, locally: .NET tests, pack + package consumer, Python tests.
#   ROSLYN_VERSIONS="3.8.0 4.8.0 5.9.0" scripts/test-all.sh   # several Roslyn lines (tests and consumer)
#   SKIP_PACK=1 scripts/test-all.sh                          # consume the packages already in ./packages
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
read -r -a versions <<<"${ROSLYN_VERSIONS:-4.8.0}"
for v in "${versions[@]}"; do
  echo "== .NET tests, Roslyn $v"
  dotnet test "$root/dotnet/CodeExec.Tests/CodeExec.Tests.csproj" -c Release -p:RoslynVersion="$v"
done
echo "== Core + adapter as source (C# 9, netstandard2.0)"
dotnet build "$root/dotnet/samples/SourceConsumer/SourceConsumer.csproj" -c Release
echo "== pack + package consumer"
[ "${SKIP_PACK:-}" = "1" ] || "$root/scripts/pack.sh" "$root/packages" >/dev/null
"$root/scripts/consume.sh" "${versions[@]}"
echo "== Python tests"
if command -v python3 >/dev/null; then python=(python3)
elif command -v python >/dev/null; then python=(python)
elif command -v py >/dev/null; then python=(py -3)   # Windows launcher
else echo "test-all: no Python 3 on PATH" >&2; exit 1
fi
"${python[@]}" -B -m unittest discover -s "$root/python/tests" -v
