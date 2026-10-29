# Code-Exec

Run short code snippets inside a live host process — game engines, editors, any .NET or Python host — with **one
contract** for C# and Python. Built for AI agents: the agent writes only the statements it needs, the result comes
back as text, and errors point at the agent's own lines.

```csharp
using System.Text;                 // optional: own usings, aliases, `using static`
var sb = new StringBuilder("ab");
class Helper { public static int Twice(int v) => v * 2; }   // types and functions are allowed
sb.Append(Helper.Twice(21));
sb.ToString()                      // last expression = the result → "ab42"
```

```python
import math
x = 21
math.floor(x * 2.5)                # last expression = the result → "52"
```

| | C# (`dotnet/`) | Python (`python/`) |
|---|---|---|
| Hosts | Unity (Mono), Godot (.NET 8+), BRUTAL, any .NET Framework / .NET app | Unreal Editor (PythonScriptPlugin), any embedded interpreter |
| Ships as | NuGet `com.IvanMurzak.CodeExec` + source-only `com.IvanMurzak.CodeExec.Roslyn` | the `code_exec` package, vendored by the host |
| Dependencies | **none** — you bring your own `Microsoft.CodeAnalysis.CSharp` (3.8 or newer) | standard library only, Python 3.10+ |
| Unloading | CoreCLR: each run in its own collectible `AssemblyLoadContext`; Mono: identical requests reuse one assembly | fresh namespace per run |

The normative contract — request/response fields, snippet semantics, error kinds — is
[`spec/contract.md`](spec/contract.md). Shared golden cases: [`fixtures/golden/`](fixtures/golden).

## Why no Roslyn dependency

Every host already has its own Roslyn constraints (Unity pins 4.8 because of `System.Collections.Immutable`; Godot and
.NET 10 apps can use the latest). So the core library has no dependencies at all, and the Roslyn adapter ships as
**source**: it compiles inside your project, against your Roslyn. CI checks that source against Roslyn 3.8, 4.0, 4.8,
4.14 and 5.x, on .NET Framework 4.8, .NET 8 and .NET 10.

## C# in two lines

```xml
<PackageReference Include="com.IvanMurzak.CodeExec" Version="0.3.1" />
<PackageReference Include="com.IvanMurzak.CodeExec.Roslyn" Version="0.3.1" />
<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" />  <!-- your choice, 3.8+ -->
```

```csharp
var runner = new CodeRunner(new RoslynCSharpCompiler(), new CodeRunnerOptions
{
    DefaultUsings = new[] { "System", "System.Linq", "UnityEngine" },   // the host's defaults
    IncludeRoot = ".ai-game-dev/snippets",                              // where `include` paths resolve
});
ExecuteResult result = await runner.ExecuteAsync(new ExecuteRequest { Code = "Application.unityVersion" });
```

- `Prepare()` compiles and is safe on a background thread; `Run()` / `RunAsync()` invoke on the calling (main) thread.
- `include` takes files and folders (recursive): the agent's helper "micro-projects", compiled with the snippet.
- Unknown simple names are imported automatically when exactly one namespace defines them (`autoImported`);
  ambiguous names are reported, never guessed. CoreLib's `Internal.*` namespaces are never imported.
- `ValueConverter` turns the result into something that outlives the snippet (Unity: ReflectorNet JSON).
- Execution guards ([§3.6](spec/contract.md#36-execution-guards)): runaway recursion stops with an error before a stack
  overflow could end the host, and loops stop with `Timeout` once the time budget is spent, also in synchronous code
  on the main thread. Budget: `TimeoutMs` on the request, else `DefaultTimeoutMs` (120 s), at most `MaxTimeoutMs`
  (0 = no cap); stack allowance `MaxStackBytes` (1 MiB); `ExecutionGuards = false` turns them off. Threads and tasks
  the snippet starts (`new Thread`, `ThreadPool`, `Task.Run`, `StartNew`) stop once its run has ended.
- Agent tools: expose `timeoutMs` as a `timeout` argument, default to about 30 s, and cap it below the tool timeout
  ([§1](spec/contract.md#1-request)).
- Output is limited before it reaches the agent ([§6](spec/contract.md#6-output-limits)): a text value, the logs (first
  and last lines kept, 2k per line) and the error, 20k characters each by default (`MaxValueChars`, `MaxLogChars`,
  `MaxLogLineChars`, `MaxErrorChars` on the request; 0 = no limit). Includes stay inside `IncludeRoot` unless `AllowIncludeOutsideRoot`.
- Hosts that load assemblies from memory (Godot's game assembly) pass a `locationResolver` to
  `LoadedAssembliesReferenceProvider`, otherwise snippets cannot see those types. On CoreCLR a snippet binds to the
  copy the process already runs (any load context) before the default context is asked, so a host's
  `AssemblyLoadContext.Default.Resolving` handler cannot hand it a second copy.

## Python

```python
import code_exec
code_exec.run("x = 21\nx * 2", include=["helpers"], include_root=".ai-game-dev/snippets", imports=["unreal"])
code_exec.run_b64(base64_json_request)   # JSON in, JSON out: one string literal from C++
code_exec.run("print('x')", max_log_chars=0)   # the same limits as C# (JSON: maxLogChars, ...); 0 = no limit
code_exec.run("while True: pass", timeout_ms=5000)   # stopped by a watchdog: errorKind Timeout (JSON: timeoutMs)
code_exec.set_default_timeout_ms(30000)               # the budget of requests without one (default 120000; 0 = none)
code_exec.set_max_timeout_ms(280000)                  # the most a request may ask for (default 0 = no cap)
code_exec.set_max_recursion_limit(3000)               # Python < 3.12: the most a snippet's sys.setrecursionlimit sets (0 = no cap)
```

## Development

```bash
scripts/test-all.sh                                  # .NET tests, pack + package consumer, Python tests
ROSLYN_VERSIONS="3.8.0 4.8.0 5.9.0" scripts/test-all.sh
dotnet test dotnet/CodeExec.Tests -p:IncludeNetFx=true -f net48   # Windows: the Mono-like path
scripts/pack.sh                                      # pack + package shape checks, publishes nothing
```

Releases: `.github/workflows/release.yml`, dispatch only. See [`AGENTS.md`](AGENTS.md).

## License

[PolyForm Shield 1.0.0](LICENSE.md). Copyright (c) 2026 Ivan Murzak.
