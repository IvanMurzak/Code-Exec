# AGENTS.md — Code-Exec

Runs short C# and Python snippets inside a live host process with one contract. Standalone: it must not depend on
Unity, Godot, Unreal, MCP, ReflectorNet or any hosted service — hosts plug in through interfaces and options.

## Layout
- `spec/contract.md` — normative request/response and snippet semantics. **The contract wins over any runtime.**
- `fixtures/golden/` — shared cases (`csharp.json`, `python.json`); `fixtures/include/` — include trees they use.
- `dotnet/CodeExec/` — Core (`netstandard2.0` + `net8.0`), **no package dependencies**.
- `dotnet/CodeExec.Roslyn/src/` — the Roslyn adapter, shipped as **source** (package `com.IvanMurzak.CodeExec.Roslyn`).
- `dotnet/CodeExec.Tests/` — xUnit; compiles the adapter source like a consumer, against `-p:RoslynVersion=…`.
- `dotnet/samples/PackageConsumer/` — consumes the packed packages with strict settings (nullable off, C# 9).
- `python/code_exec/` — the Python runtime (stdlib only); `python/tests/` — golden + behaviour tests.

## Rules
1. **Semantics change = contract change.** Edit `spec/contract.md`, then the golden fixtures, then BOTH runtimes.
2. Core stays dependency-free. Never add a `PackageReference` to `dotnet/CodeExec`.
3. The adapter source must compile with **C# 9 and Roslyn 3.8** (it is authored against them), with no warnings under
   `TreatWarningsAsErrors`, nullable on or off. Types stay `internal` (several assemblies may carry a copy).
   Every new Roslyn API needs a check that it exists in 3.8.
4. The value converter runs before an unloadable snippet is released; nothing of the snippet's own types may escape.
5. Python stays standard-library-only and Python 3.10 compatible (Unreal bundles 3.11).
6. Publishing: only `com.IvanMurzak.CodeExec` and `com.IvanMurzak.CodeExec.Roslyn` to NuGet, only by dispatching
   `.github/workflows/release.yml` (Trusted Publishing, no API key), only when the owner asks. No other publishing.

## Commands
- Everything: `scripts/test-all.sh` (`ROSLYN_VERSIONS="3.8.0 5.9.0"` for several Roslyn lines).
- .NET: `dotnet test dotnet/CodeExec.Tests -p:RoslynVersion=4.8.0`; Windows net48: add `-p:IncludeNetFx=true -f net48`.
- Pack (no publish): `scripts/pack.sh [out-dir]` — prints the version, fails on a wrong package shape.
- Python: `python -B -m unittest discover -s python/tests`.
