# Code-Exec contract (v0.1)

One request/response shape for every runtime (C# and Python) and every host (Unity, Godot, BRUTAL, Unreal, plain
.NET). **This file is normative: a runtime that disagrees with it is the bug.** Behaviour changes start here, then
`fixtures/golden/*.json`, then every runtime.

## 1. Request

| Field | Type | Meaning |
|---|---|---|
| `code` | string, required | The snippet. See §3 (C#) / §4 (Python). |
| `usings` | string[] | C# only. Extra namespaces, added to the host's defaults. Aliases (`X = A.B`) and `static A.B` are allowed. |
| `include` | string[] | Files or folders to compile/load with the snippet. Folders are recursive. Relative paths resolve against the host's include root. |
| `entry` | `{ type, method }` | C# only. Full-source mode: the code is compiled verbatim and `type.method` is invoked. |
| `parameters` | `{ name, type, value }[]` | C# only. Arguments passed to the snippet; `type` is a host type, `value` an already-deserialized object. |
| `timeoutMs` | int | C# only. Upper bound for awaiting an asynchronous result. It does **not** abort code already running. |
| `imports` | string[] | Python only. Modules the host binds into the snippet's namespace before it runs (`unreal`). |
| `includeRoot` | string | Python JSON entry point only (`run_json` / `run_b64`): folder relative `include` paths resolve against. |

## 2. Response

| Field | Type | Meaning |
|---|---|---|
| `success` | bool | |
| `value` | any | The result, converted by the host **before** the snippet's code is unloaded. `null` when there is none. |
| `logs` | `{ level, message }[]` | Output written while the snippet ran (`Console`, `print`, engine log hooks). |
| `errorKind` | enum | `None`, `InvalidRequest`, `Compile`, `EntryPointNotFound`, `Runtime`, `Timeout`, `NotAvailable`. |
| `error` | string | Human-readable error; for `Runtime`: `ExceptionType: message` plus the snippet's stack frames. |
| `diagnostics` | `{ file, line, column, id, severity, message }[]` | Compile errors in the **agent's** coordinates: `snippet.cs` / `snippet.py` or the include's relative path. |
| `autoImported` | string[] | C# only. Namespaces added automatically (§3.3). |
| `compileMs`, `executeMs` | int | Timings. |
| `cacheHit` | bool | The compiled snippet was reused. |

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
- `return;` means `return null;`; any `return expr;` returns `expr`;
- `await` anywhere in the statements (outside lambdas and local functions) makes the entry method asynchronous.

The generated names are an implementation detail (`__CodeExecSnippet.__Run`); the agent never needs them.

### 3.2. Full-source mode
With `entry`, nothing is rewritten: the code is compiled as a regular file and the named static method is invoked
(namespace-qualified type names allowed). Host defaults are not added.

### 3.3. Auto-import
When compilation fails with unknown names (`CS0246`, `CS0103`), the runtime looks the simple name up among public
types of the loaded assemblies. **Exactly one** namespace → it is added, compilation is retried, and the namespace is
reported in `autoImported`. Several namespaces → no guess: an extra diagnostic `CE0001` lists the candidates.

### 3.4. Errors
Compile errors are reported with `file` = `snippet.cs` (the agent's own line numbers) or the include's path.

### 3.5. Lifetime
Each execution is stateless. On CoreCLR the snippet's assembly is loaded into its own collectible
`AssemblyLoadContext` and unloaded after the result is converted. On Mono (Unity) assemblies cannot be unloaded;
identical requests reuse the already-loaded assembly (cache).

## 4. Python snippet semantics

- The snippet is a module body. When the **last statement is an expression**, its value is the result; otherwise `null`.
- `include` folders are put on `sys.path` for the run; modules loaded from them are dropped from `sys.modules`
  before each run, so edited helpers are picked up. `include` files are executed into the snippet's namespace first.
- `print` / `sys.stdout` / `sys.stderr` go to `logs`.
- Each run gets a fresh namespace; `sys.path` is restored afterwards. A snippet raising `SystemExit` is a `Runtime`
  error — it never stops the host.
- `SyntaxError` → `Compile` with the snippet's line/column. An exception → `Runtime`; the traceback keeps only
  frames from `snippet.py` and the includes.
- `value` conversion: `None` → null; `str`, `int`, `float`, `bool` → their `str()`; `dict` / `list` / `tuple` →
  JSON when serializable; anything else → `str()` (hosts may plug a converter, e.g. Unreal objects → path).

## 5. Value text in golden tests
Golden fixtures compare `value` as text: C# `ToString()` (`True`, `42`), Python `str()` (`True`, `42`).
