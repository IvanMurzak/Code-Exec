# Code-Exec for .NET

Run short C# snippets inside a live host process (game engines, editors, any .NET app). Built for AI agents: write
only statements, add your own usings, declare types and functions; the last expression is the result; errors point at
the snippet's own lines.

Two packages:

- **`com.IvanMurzak.CodeExec`** — the runtime. No dependencies. `netstandard2.0` (Unity / Mono, .NET Framework) and
  `net8.0` (each run in its own collectible `AssemblyLoadContext`, unloaded afterwards).
- **`com.IvanMurzak.CodeExec.Roslyn`** — source-only: the compiler adapter compiles inside your project against **your**
  `Microsoft.CodeAnalysis.CSharp` (3.8 or newer). Add that package yourself.

```csharp
var runner = new CodeRunner(new RoslynCSharpCompiler());
var result = await runner.ExecuteAsync(new ExecuteRequest { Code = "var x = 20;\nx + 22" });
// result.Success == true, result.Value == "42"
```

Contract, semantics and docs: https://github.com/IvanMurzak/Code-Exec. License: PolyForm Shield 1.0.0.
