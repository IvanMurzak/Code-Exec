# Code-Exec contract (v0.3)

One request/response shape for every runtime (C# and Python) and every host (Unity, Godot, BRUTAL, Unreal, plain
.NET). **This file is normative: a runtime that disagrees with it is the bug.** Behaviour changes start here, then
`fixtures/golden/*.json`, then every runtime.

## 1. Request

| Field | Type | Meaning |
|---|---|---|
| `code` | string, required | The snippet. See §3 (C#) / §4 (Python). |
| `usings` | string[] | C# only. Extra namespaces, added to the host's defaults. Aliases (`X = A.B`) and `static A.B` are allowed. |
| `include` | string[] | Files or folders to compile/load with the snippet (§1.1). |
| `entry` | `{ type, method }` | C# only. Full-source mode: the code is compiled verbatim and `type.method` is invoked. |
| `parameters` | `{ name, type, value }[]` | C# only. Arguments passed to the snippet; `type` is a host type, `value` an already-deserialized object. |
| `timeoutMs` | int | C# only. Upper bound for awaiting an asynchronous result. It does **not** abort code already running. |
| `imports` | string[] | Python only. Modules the host binds into the snippet's namespace before it runs (`unreal`). |
| `includeRoot` | string | Python JSON entry point only (`run_json` / `run_b64`): folder relative `include` paths resolve against. |
| `includeOutsideRoot` | bool | Python JSON entry point only: the host accepts includes outside the include root (§1.1). Default `false`. |
| `maxValueChars` | int | Output limit for a text `value` (§6). Default `20000`. |
| `maxLogChars` | int | Output limit for all `logs` together (§6). Default `20000`. |
| `maxLogLineChars` | int | Output limit for one log message (§6). Default `2000`. |
| `maxErrorChars` | int | Output limit for `error` (§6). Default `20000`. |

Every field but `code` is optional. For the four limits, `0` means no limit; a negative (or non-integer) limit is
`InvalidRequest`. A request without them gets the defaults. A field of the wrong JSON type is `InvalidRequest`.

### 1.1. Includes
- Relative paths resolve against the host's include root. An include must lie **inside** that root (the root itself
  included): a path that leaves it (`../x`, an absolute path elsewhere) is `InvalidRequest`, unless the host allows it
  (C# `CodeRunnerOptions.AllowIncludeOutsideRoot`, Python `include_outside_root=True` / `includeOutsideRoot`). Includes
  are the agent's helper code, not a way to compile arbitrary project files.
- A file must be source of the runtime's language: `.cs` (C#), `.py` (Python); anything else is `InvalidRequest`.
  Folders are recursive and contribute only such files (C#: `*.cs`; Python: the folder goes on `sys.path`).
- Diagnostics and stack frames name an include by its path relative to the root (`/` separators), or by its absolute
  path when the host allowed one outside the root.

## 2. Response

| Field | Type | Meaning |
|---|---|---|
| `success` | bool | |
| `value` | any | The result, converted by the host **before** the snippet's code is unloaded. `null` when there is none. |
| `returnType` | string | C# only. Result type, `Task<T>` → `T`: `System.Void` when nothing is returned; for a snippet whose result is its last expression, that expression's static type (`System.Int32`, `System.Collections.Generic.List<System.Int32>`); `System.Object` when a snippet (also) returns through `return` statements, `return;` included; the method's type in full-source mode. |
| `logs` | `{ level, message }[]` | Output written while the snippet ran (`Console`, `print`, engine log hooks). |
| `errorKind` | enum | `None`, `InvalidRequest`, `Compile`, `EntryPointNotFound`, `Runtime`, `Timeout`, `NotAvailable`. |
| `error` | string | Human-readable error; for `Runtime`: `ExceptionType: message` plus the snippet's stack frames. |
| `diagnostics` | `{ file, line, column, id, severity, message }[]` | Compile errors in the **agent's** coordinates: `snippet.cs` / `snippet.py` or the include's relative path (§3.4). At most 20, then one `CE0002` entry counting the rest. |
| `autoImported` | string[] | C# only. Namespaces added automatically (§3.3). |
| `compileMs`, `executeMs` | int | Timings. |
| `cacheHit` | bool | The compiled snippet was reused. |
| `truncated` | string[] | The fields an output limit shortened (§6), in this order: `value`, `logs`, `error`. Empty when nothing was. |

## 3. C# snippet semantics

### 3.1. Shape
The snippet is parsed as C# **script** code and rewritten into one ordinary compilation unit:

- `using` directives (including aliases and `using static`) go to the top, after the host defaults and `usings`;
- type declarations (class, struct, record, interface, enum, delegate, namespace) stay top-level;
- members **without** modifiers live in the entry method's body, in source order, so they see the snippet's locals:
  `var x = 1;` is a local variable and `int Twice(int n) => n * 2;` a local function;
- members **with** modifiers (methods, properties, indexers, events, fields: `static int counter = 3;`) become static
  members of the generated class;
- the **last statement** (trailing local functions aside), when it is an expression without `;`, or an expression that
  is not a valid statement (`x * 2;`), is returned; if that expression is `void`, it runs as a plain statement and the
  result is `null`;
- a snippet with neither such a result expression nor a `return expr;` returns nothing: its entry method is `void`
  (`returnType` = `System.Void`); otherwise `return;` means `return null;` and any `return expr;` returns `expr`;
- `await` anywhere in the statements (outside lambdas and local functions) makes the entry method asynchronous.

The generated names are an implementation detail (`__CodeExecSnippet.__Run`); the agent never needs them.

### 3.2. Full-source mode
With `entry`, nothing is rewritten: the code is compiled as a regular file and the named static method is invoked
(namespace-qualified type names allowed). Host defaults are not added. Among overloads, the most specific one for the
parameters' types is invoked; with none fitting, the only method of that name and parameter count. Anything else
(ambiguous, several that do not fit) is `EntryPointNotFound`.

### 3.3. Auto-import
When compilation fails with unknown names (`CS0246`, `CS0103`), the runtime looks the simple name up among public
types of the loaded assemblies, except the runtime's own `Internal` / `Internal.*` namespaces in CoreLib
(`Internal.Console` writes past log capture). **Exactly one** namespace → it is added, compilation is retried, and the namespace is
reported in `autoImported`. Several namespaces → no guess: an extra diagnostic `CE0001` lists the candidates.

### 3.4. Errors
Compile errors are reported with `file` = `snippet.cs` (the agent's own line numbers) or the include's path. An error
in a namespace the runtime adds (the host's defaults and the request's `usings`) has `file` = `usings`, line and column
`0`, and a message that names it: `using 'No.Such.Namespace': The type or namespace name 'No' could not be found ...`.
Any exception the snippet throws (or its result's conversion throws) is a `Runtime` error: `ExceptionType: message`,
the snippet's frames, then each inner exception the same way after ` ---> ` (at most 5). An exception whose `Message`
itself throws is reported as `ExceptionType: <unprintable message>`. What .NET cannot catch at all (a stack overflow)
still ends the process: a snippet that may recurse deeply must bound its own depth.

Host defaults that collide (`System` and `UnityEngine` both define `Object` and `Random`: `CS0104`) are the host's to
settle with alias defaults, e.g. `Object = UnityEngine.Object`, `Random = UnityEngine.Random`.

### 3.5. Lifetime
Each execution is stateless. On CoreCLR the snippet's assembly is loaded into its own collectible
`AssemblyLoadContext` and unloaded after the result is converted; its references bind to the assemblies the process
already runs (any load context still alive; same identity first, else the same name at a version no lower than
referenced; ties: the default context, then the higher version, then the later loaded) before the default context is
asked, so the snippet sees the host's live statics, never a second copy. On Mono (Unity) assemblies cannot be unloaded;
identical requests reuse the already-loaded assembly (cache) — unless the snippet (or an include) declares static state
of its own (a static field, auto-property or field-like event in one of its types): then every run loads a fresh copy,
so no run ever sees another run's statics. Such a snippet costs one assembly per run until the host's domain reloads.
A `static readonly` field (or a getter-only static auto-property) is **not** state when its declared type is immutable:
`string`, a primitive, an enum, `decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`, a nullable of one of
these, or a read-only / immutable collection of immutable elements (`IReadOnlyCollection<T>`, `IReadOnlyList<T>`,
`IReadOnlyDictionary<K,V>`, `IReadOnlySet<T>`, `ReadOnlyCollection<T>`, `ReadOnlyDictionary<K,V>`,
`System.Collections.Immutable.*`). So a lookup table like `static readonly IReadOnlyDictionary<string, int> Map` keeps
the snippet cached. Arrays are mutable (`static readonly int[]` is state). Two consequences of reuse on Mono, by design:
such a field's initializer runs once per loaded copy (`static readonly DateTime Started = DateTime.Now` keeps its first
value), and a cast from the declared read-only type back to a mutable one is not detected.

## 4. Python snippet semantics

- The snippet is a module body. When the **last statement is an expression**, its value is the result; otherwise `null`.
- `include` folders are put on `sys.path` for the run; modules loaded from them are dropped from `sys.modules`
  before each run, so edited helpers are picked up. `include` files are executed into the snippet's namespace first.
- `print` / `sys.stdout` / `sys.stderr` go to `logs`.
- Each run gets a fresh namespace; `sys.path`, the working directory and the recursion limit are restored afterwards,
  and the modules the run imported from include folders are dropped (host modules imported before stay).
- `include` files are read as Python reads source files: UTF-8, or the encoding named by a UTF-8 BOM or a PEP 263
  coding declaration. A file that does not decode (or names an unknown or non-text encoding) is `InvalidRequest`.
- `SyntaxError` → `Compile` with the snippet's line/column. Any exception → `Runtime`, **including every
  `BaseException`** (`SystemExit`, `KeyboardInterrupt`, `GeneratorExit`, custom subclasses): a snippet never stops or
  interrupts the host. The traceback keeps only frames from `snippet.py` and the includes, then follows the chain (at
  most 5): `Caused by: ...` for `raise ... from cause`, `Raised while handling: ...` for an implicit `__context__`. An
  exception whose `str()` raises is reported as `ExceptionType: <unprintable message>`.
- `value` conversion: `None` → null; `str`, `int`, `float`, `bool` → their `str()`; `dict` / `list` / `tuple` →
  JSON when serializable; anything else → `str()` (hosts may plug a converter, e.g. Unreal objects → path).

## 5. Value text in golden tests
Golden fixtures compare `value` as text: C# `ToString()` (`True`, `42`), Python `str()` (`True`, `42`).

## 6. Output limits
A response goes straight into an agent's context, so a runaway `print` loop or a huge value must not flood it. The
runtime applies the request's limits (§1) before it answers; hosts may limit further (their own logs, other fields).
Lengths are in characters: UTF-16 code units in C#, code points in Python. A limit of `0` disables that limit.

- **Clip.** A text longer than its limit `N` keeps its first `N` characters followed by
  `... [truncated: N of M characters shown; HINT]` (`M` = the full length). C# never splits a surrogate pair: it then
  keeps `N - 1` characters and says so.
- **`value`** — clipped to `maxValueChars` when the converted value is a string; hint `return a slice or a summary`.
  A host converter that returns something else (an object it serializes later) limits that itself.
- **`error`** — clipped to `maxErrorChars`; hint `raise a shorter message`. Before that, a `Runtime` traceback with more
  than 20 of the agent's frames keeps the first 10 and the last 10, with one line `... K frame(s) left out` between.
- **`logs`** — every message is clipped to `maxLogLineChars` (hint `print less`). An entry then costs its message length
  plus 12 (its level and line break as hosts print them). When the entries cost more than `maxLogChars` in total, the
  runtime keeps the longest run of first entries that costs at most `floor(maxLogChars * 3 / 4)`, then the longest run
  of last entries that keeps the total within `maxLogChars`, and puts between them one entry
  `{ "level": "warning", "message": "... K log line(s) left out (output over maxLogChars characters): print less, or
  return a summary" }` (with the numbers filled in; this entry costs nothing). Runtimes apply this as lines arrive, so
  a snippet that prints line after line without end does not grow the host's memory either (one unfinished line is
  held until it ends).
- `truncated` lists `value`, `logs` and `error` when their limit changed them (a clipped log line counts as `logs`).

## Changelog

- **v0.3.1** — C#: `static readonly` fields of immutable types are not static state (§3.5). Python is unchanged.
- **v0.3** — output limits (§6): request fields `maxValueChars`, `maxLogChars`, `maxLogLineChars`, `maxErrorChars`
  (defaults 20000 / 20000 / 2000 / 20000, the caps Unreal-MCP and Godot-MCP applied on their side) and response field
  `truncated`; long tracebacks keep their first and last 10 frames; at most 20 diagnostics. Includes stay inside the
  include root unless the host allows otherwise, and must be `.cs` / `.py` files (§1.1). Errors report inner / chained
  exceptions. C#: a bad `using` is reported as itself (`file` = `usings`); a snippet with static state of its own never
  shares it between runs on Mono. Python: every `BaseException` (`KeyboardInterrupt`, `GeneratorExit`, ...) is a
  `Runtime` error, the working directory is restored, includes honour a BOM / coding declaration, a malformed JSON
  request field is `InvalidRequest`. Both runtimes: an exception whose message cannot be printed is still reported.

- **v0.2.1** — auto-import skips CoreLib's `Internal.*` namespaces; CoreCLR snippets bind to the already-loaded host
  assemblies (any load context) and always reference System.Runtime / System.Console / System.Collections /
  System.Linq, loaded or not, when the runtime directory is on disk.
- **v0.2** — `returnType` (C#). A snippet that returns nothing compiles to a `void` (or `async Task`) entry method.
- **v0.1** — first contract.
