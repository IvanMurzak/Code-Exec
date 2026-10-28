#!/usr/bin/env bash
# Packs com.IvanMurzak.CodeExec + com.IvanMurzak.CodeExec.Roslyn into $1 (default: ./packages) and checks the
# source-only package's shape. Prints the version on stdout. Publishes nothing.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-$root/packages}"
mkdir -p "$out"
dotnet pack "$root/dotnet/CodeExec/CodeExec.csproj" -c Release -o "$out" >&2
dotnet pack "$root/dotnet/CodeExec.Roslyn/CodeExec.Roslyn.csproj" -c Release -o "$out" >&2
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/dotnet/Directory.Build.props")"
roslyn="$out/com.IvanMurzak.CodeExec.Roslyn.$version.nupkg"
listing="$(unzip -l "$roslyn")"
for f in src/RoslynCSharpCompiler.cs src/SnippetWriter.cs src/GuardWriter.cs build/com.IvanMurzak.CodeExec.Roslyn.props LICENSE.md; do
  grep -q " $f\$" <<<"$listing" || { echo "pack check: $f missing from $roslyn" >&2; exit 1; }
done
if grep -q " lib/" <<<"$listing"; then echo "pack check: $roslyn must not contain lib/ (source-only)" >&2; exit 1; fi
nuspec="$(unzip -p "$roslyn" com.IvanMurzak.CodeExec.Roslyn.nuspec)"
if grep -qi "id=\"Microsoft.CodeAnalysis" <<<"$nuspec"; then echo "pack check: Roslyn must not be a dependency" >&2; exit 1; fi
grep -q 'id="com.IvanMurzak.CodeExec"' <<<"$nuspec" || { echo "pack check: missing dependency on com.IvanMurzak.CodeExec" >&2; exit 1; }
core_nuspec="$(unzip -p "$out/com.IvanMurzak.CodeExec.$version.nupkg" com.IvanMurzak.CodeExec.nuspec)"
if grep -q "<dependency " <<<"$core_nuspec"; then echo "pack check: com.IvanMurzak.CodeExec must have no dependencies" >&2; exit 1; fi
echo "$version"   # stdout carries only the version (release.yml reads it)
